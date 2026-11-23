using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// Hands one received SQS message to the framework's <see cref="IBrokerReceivePipeline"/> and carries
/// out the <see cref="BrokerDisposition"/> it returns. It decides nothing itself:
/// <list type="bullet">
///   <item><see cref="BrokerDisposition.Completed"/>: delete the message.</item>
///   <item><see cref="BrokerDisposition.Redeliver"/>: send a copy to the host's queue with
///   <c>DelaySeconds</c> = the delay (body and every header, with the redelivery count set), then delete
///   the original. Sending first means a crash in between yields a duplicate the inbox absorbs, never a
///   lost message.</item>
///   <item><see cref="BrokerDisposition.DeadLetter"/>: send a copy to the <c>{queue}-error</c> queue with
///   the reason and the detail in its headers, then delete the original.</item>
///   <item>The host stopping (the pipeline's <see cref="OperationCanceledException"/>): reset the
///   message's visibility to zero, best effort, so it is delivered again after the restart (or when
///   its visibility timeout runs out, should the reset fail).</item>
/// </list>
/// While the pipeline runs (in-process retries included) a heartbeat pushes the message's visibility
/// timeout out every <see cref="HeartbeatInterval"/>, so a slow message is never handed to a second
/// consumer. A message the pipeline has finished with is settled with no cancellation token: the work is
/// done, and settling it during shutdown is what keeps it from running twice.
/// </summary>
internal sealed partial class AmazonSqsDeliveryHandler
{
    /// <summary>
    /// The visibility timeout a received message gets, and every heartbeat renews, in seconds. Short, so
    /// a message whose consumer died is redelivered within a minute; the heartbeat keeps a live one hidden.
    /// </summary>
    internal const int VisibilityTimeoutSeconds = 60;

    /// <summary>How often the heartbeat renews the visibility timeout: a third of it, so two renewals can fail in a row.</summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(VisibilityTimeoutSeconds / 3);

    private readonly IBrokerReceivePipeline _pipeline;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="AmazonSqsDeliveryHandler"/> class.</summary>
    /// <param name="pipeline">The framework's receive pipeline.</param>
    /// <param name="timeProvider">The clock the heartbeat waits on.</param>
    /// <param name="logger">The logger a failed heartbeat or visibility reset is reported to.</param>
    public AmazonSqsDeliveryHandler(IBrokerReceivePipeline pipeline, TimeProvider timeProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _pipeline = pipeline;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Processes and settles one received message.</summary>
    /// <param name="message">The received message.</param>
    /// <param name="settlement">The calls that settle it.</param>
    /// <param name="cancellationToken">Fires when the host is stopping.</param>
    /// <returns>A task that completes when the message is settled.</returns>
    public async Task HandleAsync(Message message, IAmazonSqsSettlement settlement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(settlement);

        var headers = AmazonSqsMessageMapping.ToHeaders(message);

        BrokerDisposition? disposition = null;
        using (var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var heartbeat = RunHeartbeatAsync(message, settlement, heartbeatStop.Token);
            try
            {
                disposition = await _pipeline.ProcessAsync(AmazonSqsMessageMapping.ToIncoming(message, headers), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The host is stopping: the message is released below, unsettled.
            }
            finally
            {
                await heartbeatStop.CancelAsync().ConfigureAwait(false);
                await heartbeat.ConfigureAwait(false);
            }
        }

        if (disposition is null)
        {
            await ReleaseBestEffortAsync(message, settlement).ConfigureAwait(false);
            return;
        }

        var body = message.Body ?? string.Empty;
        switch (disposition)
        {
            case BrokerDisposition.Completed:
                await settlement.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                break;

            case BrokerDisposition.Redeliver redeliver:
                await settlement.SendToQueueAsync(
                    body,
                    AmazonSqsMessageMapping.RedeliveryHeaders(headers, redeliver.RedeliveryCount),
                    AmazonSqsMessageMapping.ToDelaySeconds(redeliver.Delay),
                    CancellationToken.None).ConfigureAwait(false);
                await settlement.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                break;

            case BrokerDisposition.DeadLetter deadLetter:
                await settlement.SendToErrorQueueAsync(
                    body,
                    AmazonSqsMessageMapping.DeadLetterHeaders(headers, deadLetter.Reason, deadLetter.Detail),
                    CancellationToken.None).ConfigureAwait(false);
                await settlement.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                break;

            default:
                // A disposition this transport does not know is a framework bug: throwing leaves the
                // message to reappear when its visibility timeout runs out, rather than guessing.
                throw new InvalidOperationException($"Unknown broker disposition '{disposition.GetType().Name}'.");
        }
    }

    private async Task RunHeartbeatAsync(Message message, IAmazonSqsSettlement settlement, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, _timeProvider, stop).ConfigureAwait(false);
                await settlement.ChangeVisibilityAsync(VisibilityTimeoutSeconds, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed renewal must not fail the message: the next beat retries it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogHeartbeatFailed(_logger, ex, message.MessageId);
            }
        }
    }

    private async Task ReleaseBestEffortAsync(Message message, IAmazonSqsSettlement settlement)
    {
        try
        {
            await settlement.ChangeVisibilityAsync(0, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort during shutdown: an unreleased message reappears once its visibility timeout runs out anyway.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReleaseFailed(_logger, ex, message.MessageId);
        }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Could not release SQS message {MessageId} while the host was stopping; it is delivered again when its visibility timeout runs out.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, string? messageId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Could not extend the visibility timeout of SQS message {MessageId}; the next heartbeat tries again.")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception exception, string? messageId);
}
