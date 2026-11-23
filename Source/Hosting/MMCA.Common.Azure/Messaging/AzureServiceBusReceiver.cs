using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// The hosted receiver of the Azure Service Bus transport. In the background, so a broker that is
/// still warming up (or briefly unreachable) never fails host startup, it provisions the topology
/// when <c>MessageBus:ProvisionTopology</c> is on (retrying with backoff until it succeeds), then
/// runs a <see cref="ServiceBusProcessor"/> on the host's queue that hands every message to
/// <see cref="AzureServiceBusDeliveryHandler"/>. A host that consumes no event creates no queue and
/// runs no processor. Stopping the host stops the processor, which cancels in-flight messages so they
/// are abandoned and delivered again after the restart.
/// </summary>
internal sealed partial class AzureServiceBusReceiver : BackgroundService
{
    /// <summary>Concurrent message handlers when <c>MessageBus:ConcurrentMessageLimit</c> is unset.</summary>
    internal const int DefaultMaxConcurrentCalls = 16;

    /// <summary>
    /// Prefetched messages when <c>MessageBus:PrefetchCount</c> is unset. Zero (the SDK's own default)
    /// on purpose: a prefetched message's lock runs while it waits and is not renewed until a handler
    /// takes it, and the receive pipeline can hold a handler for tens of seconds of in-process retry.
    /// </summary>
    internal const int DefaultPrefetchCount = 0;

    /// <summary>
    /// How long the processor keeps renewing a message's lock: far beyond the in-process retry budget
    /// (1 + 2 + 4 + 8 + 16 seconds with the defaults, plus the handlers' own time), so a message being
    /// retried is never handed to a second consumer.
    /// </summary>
    internal static readonly TimeSpan MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan FirstProvisioningBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxProvisioningBackoff = TimeSpan.FromMinutes(1);

    private readonly AzureServiceBusConnection _connection;
    private readonly BrokerTopology _topology;
    private readonly AzureServiceBusDeliveryHandler _handler;
    private readonly AzureServiceBusTopologyProvisioner? _provisioner;
    private readonly MessageBusSettings _settings;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="AzureServiceBusReceiver"/> class.</summary>
    /// <param name="connection">The shared clients.</param>
    /// <param name="topology">The host's queue and subscriptions.</param>
    /// <param name="handler">Processes and settles each message.</param>
    /// <param name="provisioner">Creates the topology, or <see langword="null"/> when provisioning is off.</param>
    /// <param name="settings">The bound <c>MessageBus</c> section (backpressure).</param>
    /// <param name="logger">The receiver's logger.</param>
    public AzureServiceBusReceiver(
        AzureServiceBusConnection connection,
        BrokerTopology topology,
        AzureServiceBusDeliveryHandler handler,
        AzureServiceBusTopologyProvisioner? provisioner,
        MessageBusSettings settings,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _topology = topology;
        _handler = handler;
        _provisioner = provisioner;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The processor options for the host's queue: manual settlement and the backpressure knobs.</summary>
    /// <param name="settings">The bound <c>MessageBus</c> section.</param>
    /// <returns>The processor options.</returns>
    internal static ServiceBusProcessorOptions BuildProcessorOptions(MessageBusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ServiceBusProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            AutoCompleteMessages = false,
            PrefetchCount = BrokerTransportPolicies.ResolvePrefetchCount(settings) ?? DefaultPrefetchCount,
            MaxConcurrentCalls = BrokerTransportPolicies.ResolveConcurrentMessageLimit(settings) ?? DefaultMaxConcurrentCalls,
            MaxAutoLockRenewalDuration = MaxAutoLockRenewalDuration,
        };
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_topology.Subscriptions.Count == 0)
        {
            return;
        }

        var queue = AzureServiceBusEntityNames.Queue(_topology.QueueName);

        if (_provisioner is not null && !await ProvisionWithRetryAsync(stoppingToken).ConfigureAwait(false))
        {
            return;
        }

        var queueSender = _connection.Client.CreateSender(queue);
        await using (queueSender.ConfigureAwait(false))
        {
            var processor = _connection.Client.CreateProcessor(queue, BuildProcessorOptions(_settings));
            await using (processor.ConfigureAwait(false))
            {
                processor.ProcessMessageAsync += args =>
                    _handler.HandleAsync(args.Message, new ProcessorSettlement(args, queueSender), args.CancellationToken);
                processor.ProcessErrorAsync += OnProcessErrorAsync;

                try
                {
                    await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
                    LogReceiving(_logger, queue, _topology.Subscriptions.Count);
                    await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // The host is stopping.
                }
                finally
                {
                    if (processor.IsProcessing)
                    {
                        await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Provisions until it succeeds or the host stops, backing off from 2 seconds to a minute between
    /// attempts. Each failure is logged; none fails the host.
    /// </summary>
    /// <returns><see langword="false"/> when the host stopped first.</returns>
    private async Task<bool> ProvisionWithRetryAsync(CancellationToken stoppingToken)
    {
        var backoff = FirstProvisioningBackoff;
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await _provisioner!.ProvisionAsync(_topology, stoppingToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
#pragma warning disable CA1031 // Background provisioning must never crash the host: it logs and retries.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogProvisioningFailed(_logger, ex, _topology.QueueName, attempt, backoff.TotalSeconds);
            }

            try
            {
                await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxProvisioningBackoff.Ticks));
        }
    }

    private Task OnProcessErrorAsync(ProcessErrorEventArgs args)
    {
        // A cancellation raised while the processor stops is the shutdown itself, not an error.
        if (args.Exception is not OperationCanceledException || !args.CancellationToken.IsCancellationRequested)
        {
            LogProcessorError(_logger, args.Exception, args.EntityPath, args.ErrorSource.ToString());
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Receiving from Azure Service Bus queue '{Queue}' ({SubscriptionCount} subscriptions).")]
    private static partial void LogReceiving(ILogger logger, string queue, int subscriptionCount);

    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Provisioning the Azure Service Bus topology for queue '{Queue}' failed (attempt {Attempt}); retrying in {BackoffSeconds} s.")]
    private static partial void LogProvisioningFailed(ILogger logger, Exception exception, string queue, int attempt, double backoffSeconds);

    [LoggerMessage(EventId = 22, Level = LogLevel.Error, Message = "Azure Service Bus processor error on '{EntityPath}' ({ErrorSource}).")]
    private static partial void LogProcessorError(ILogger logger, Exception exception, string entityPath, string errorSource);

    /// <summary>Settles a processor-delivered message, scheduling redeliveries on the host's queue.</summary>
    private sealed class ProcessorSettlement(ProcessMessageEventArgs args, ServiceBusSender queueSender) : IAzureServiceBusSettlement
    {
        public Task CompleteAsync(CancellationToken cancellationToken) =>
            args.CompleteMessageAsync(args.Message, cancellationToken);

        public Task DeadLetterAsync(string reason, string? description, CancellationToken cancellationToken) =>
            args.DeadLetterMessageAsync(args.Message, reason, description, cancellationToken);

        public Task AbandonAsync(CancellationToken cancellationToken) =>
            args.AbandonMessageAsync(args.Message, propertiesToModify: null, cancellationToken);

        public Task ScheduleOnQueueAsync(ServiceBusMessage message, DateTimeOffset enqueueTime, CancellationToken cancellationToken) =>
            queueSender.ScheduleMessageAsync(message, enqueueTime, cancellationToken);
    }
}
