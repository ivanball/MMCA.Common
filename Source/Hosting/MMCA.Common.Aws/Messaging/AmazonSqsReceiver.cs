using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// The hosted receiver of the Amazon SQS transport. In the background, so a broker that is still
/// starting (or briefly unreachable) never fails host startup, it resolves the host's queues (creating
/// the topology first when <c>MessageBus:ProvisionTopology</c> is on), retrying with backoff until that
/// succeeds, then long-polls the queue and hands every message to <see cref="AmazonSqsDeliveryHandler"/>.
/// A receive never asks for more messages than there are free handler slots, so no received message
/// waits with its visibility timeout running. A host that consumes no event creates no queue and polls
/// nothing. Stopping the host cancels in-flight messages, which are released back to the queue.
/// </summary>
internal sealed partial class AmazonSqsReceiver : BackgroundService
{
    /// <summary>Concurrent message handlers when <c>MessageBus:ConcurrentMessageLimit</c> is unset.</summary>
    internal const int DefaultConcurrentMessageLimit = 16;

    /// <summary>The most messages one SQS receive returns, and the batch size when <c>MessageBus:PrefetchCount</c> is unset.</summary>
    internal const int MaxMessagesPerReceive = 10;

    /// <summary>The long-poll wait of each receive, in seconds (the SQS maximum).</summary>
    internal const int WaitTimeSeconds = 20;

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);

    private readonly IAmazonSQS _sqs;
    private readonly BrokerTopology _topology;
    private readonly AmazonSqsDeliveryHandler _handler;
    private readonly AmazonSqsTopologyProvisioner _provisioner;
    private readonly MessageBusSettings _settings;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="AmazonSqsReceiver"/> class.</summary>
    /// <param name="sqs">The SQS client.</param>
    /// <param name="topology">The host's queue and subscriptions.</param>
    /// <param name="handler">Processes and settles each message.</param>
    /// <param name="provisioner">Resolves (and when provisioning, creates) the queues and subscriptions.</param>
    /// <param name="settings">The bound <c>MessageBus</c> section (backpressure).</param>
    /// <param name="logger">The receiver's logger.</param>
    public AmazonSqsReceiver(
        IAmazonSQS sqs,
        BrokerTopology topology,
        AmazonSqsDeliveryHandler handler,
        AmazonSqsTopologyProvisioner provisioner,
        MessageBusSettings settings,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(sqs);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _sqs = sqs;
        _topology = topology;
        _handler = handler;
        _provisioner = provisioner;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>How many messages may be in flight at once: <c>MessageBus:ConcurrentMessageLimit</c>, else 16.</summary>
    /// <param name="settings">The bound <c>MessageBus</c> section.</param>
    /// <returns>The concurrency limit.</returns>
    internal static int ResolveConcurrency(MessageBusSettings settings) =>
        BrokerTransportPolicies.ResolveConcurrentMessageLimit(settings) ?? DefaultConcurrentMessageLimit;

    /// <summary>The most messages one receive asks for: <c>MessageBus:PrefetchCount</c>, else 10, never above 10.</summary>
    /// <param name="settings">The bound <c>MessageBus</c> section.</param>
    /// <returns>The batch size, 1 to 10.</returns>
    internal static int ResolveBatchSize(MessageBusSettings settings) =>
        Math.Clamp(BrokerTransportPolicies.ResolvePrefetchCount(settings) ?? MaxMessagesPerReceive, 1, MaxMessagesPerReceive);

    /// <summary>The long-poll receive request for up to <paramref name="maxMessages"/> messages with every attribute.</summary>
    /// <param name="queueUrl">The host's queue.</param>
    /// <param name="maxMessages">The most messages to return.</param>
    /// <returns>The request.</returns>
    internal static ReceiveMessageRequest BuildReceiveRequest(string queueUrl, int maxMessages) => new()
    {
        QueueUrl = queueUrl,
        MaxNumberOfMessages = maxMessages,
        WaitTimeSeconds = WaitTimeSeconds,
        VisibilityTimeout = AmazonSqsDeliveryHandler.VisibilityTimeoutSeconds,
        MessageAttributeNames = ["All"],
    };

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_topology.Subscriptions.Count == 0)
        {
            return;
        }

        var queues = await ProvisionWithRetryAsync(stoppingToken).ConfigureAwait(false);
        if (queues is not { } resolved)
        {
            return;
        }

        var concurrency = ResolveConcurrency(_settings);
        var batchSize = ResolveBatchSize(_settings);
        using var slots = new SemaphoreSlim(concurrency, concurrency);

        LogReceiving(_logger, resolved.QueueUrl, _topology.Subscriptions.Count, concurrency);
        try
        {
            await ReceiveLoopAsync(resolved.QueueUrl, resolved.ErrorQueueUrl, slots, batchSize, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            // Drain: every in-flight handler returns its slot when it finishes (cancelled by the stop,
            // each releases its message), so holding every slot means nothing is left running.
            for (var i = 0; i < concurrency; i++)
            {
                await slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ReceiveLoopAsync(string queueUrl, string errorQueueUrl, SemaphoreSlim slots, int batchSize, CancellationToken stoppingToken)
    {
        var backoff = FirstBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await slots.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var acquired = 1;
            // Only slots that are free right now (a zero timeout): the batch never waits for more.
            while (acquired < batchSize && await slots.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                acquired++;
            }

            List<Message> messages;
            try
            {
                var response = await _sqs.ReceiveMessageAsync(BuildReceiveRequest(queueUrl, acquired), stoppingToken).ConfigureAwait(false);
                messages = response.Messages ?? [];
                backoff = FirstBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                slots.Release(acquired);
                return;
            }
#pragma warning disable CA1031 // The receive loop must never crash the host: it logs and backs off.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                slots.Release(acquired);
                LogReceiveFailed(_logger, ex, queueUrl, backoff.TotalSeconds);
                if (!await DelayAsync(backoff, stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                backoff = NextBackoff(backoff);
                continue;
            }

            if (messages.Count < acquired)
            {
                slots.Release(acquired - messages.Count);
            }

            foreach (var message in messages)
            {
                var settlement = new QueueSettlement(_sqs, queueUrl, errorQueueUrl, message.ReceiptHandle);
                _ = Task.Run(() => ProcessAsync(message, settlement, slots, stoppingToken), CancellationToken.None);
            }
        }
    }

    private async Task ProcessAsync(Message message, QueueSettlement settlement, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            await _handler.HandleAsync(message, settlement, stoppingToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // One message failing to settle must not stop the others: it reappears when its visibility timeout runs out.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSettleFailed(_logger, ex, message.MessageId);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// Resolves the queues until it succeeds or the host stops, backing off from 2 seconds to a minute
    /// between attempts. Each failure is logged; none fails the host.
    /// </summary>
    /// <returns>The queue URLs, or <see langword="null"/> when the host stopped first.</returns>
    private async Task<(string QueueUrl, string ErrorQueueUrl)?> ProvisionWithRetryAsync(CancellationToken stoppingToken)
    {
        var backoff = FirstBackoff;
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await _provisioner.ProvisionAsync(_topology, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return null;
            }
#pragma warning disable CA1031 // Background provisioning must never crash the host: it logs and retries.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogProvisioningFailed(_logger, ex, _topology.QueueName, attempt, backoff.TotalSeconds);
            }

            if (!await DelayAsync(backoff, stoppingToken).ConfigureAwait(false))
            {
                return null;
            }

            backoff = NextBackoff(backoff);
        }
    }

    private static TimeSpan NextBackoff(TimeSpan backoff) => TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Receiving from Amazon SQS queue '{QueueUrl}' ({SubscriptionCount} subscriptions, up to {Concurrency} messages at once).")]
    private static partial void LogReceiving(ILogger logger, string queueUrl, int subscriptionCount, int concurrency);

    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Provisioning the Amazon SQS topology for queue '{Queue}' failed (attempt {Attempt}); retrying in {BackoffSeconds} s.")]
    private static partial void LogProvisioningFailed(ILogger logger, Exception exception, string queue, int attempt, double backoffSeconds);

    [LoggerMessage(EventId = 22, Level = LogLevel.Error, Message = "Receiving from Amazon SQS queue '{QueueUrl}' failed; retrying in {BackoffSeconds} s.")]
    private static partial void LogReceiveFailed(ILogger logger, Exception exception, string queueUrl, double backoffSeconds);

    [LoggerMessage(EventId = 23, Level = LogLevel.Error, Message = "Settling Amazon SQS message {MessageId} failed; it is delivered again when its visibility timeout runs out.")]
    private static partial void LogSettleFailed(ILogger logger, Exception exception, string? messageId);

    /// <summary>Settles one received message on the host's queue and its error queue.</summary>
    private sealed class QueueSettlement(IAmazonSQS sqs, string queueUrl, string errorQueueUrl, string receiptHandle) : IAmazonSqsSettlement
    {
        public Task DeleteAsync(CancellationToken cancellationToken) =>
            sqs.DeleteMessageAsync(queueUrl, receiptHandle, cancellationToken);

        public Task SendToQueueAsync(string body, IReadOnlyDictionary<string, string> headers, int delaySeconds, CancellationToken cancellationToken) =>
            sqs.SendMessageAsync(AmazonSqsMessageMapping.ToSendRequest(queueUrl, body, headers, delaySeconds), cancellationToken);

        public Task SendToErrorQueueAsync(string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken) =>
            sqs.SendMessageAsync(AmazonSqsMessageMapping.ToSendRequest(errorQueueUrl, body, headers, delaySeconds: 0), cancellationToken);

        public Task ChangeVisibilityAsync(int visibilityTimeoutSeconds, CancellationToken cancellationToken) =>
            sqs.ChangeMessageVisibilityAsync(queueUrl, receiptHandle, visibilityTimeoutSeconds, cancellationToken);
    }
}
