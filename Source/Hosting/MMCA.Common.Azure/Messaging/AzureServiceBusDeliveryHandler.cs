using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// Hands one delivered Service Bus message to the framework's <see cref="IBrokerReceivePipeline"/> and
/// carries out the <see cref="BrokerDisposition"/> it returns. It decides nothing itself:
/// <list type="bullet">
///   <item><see cref="BrokerDisposition.Completed"/>: complete the message.</item>
///   <item><see cref="BrokerDisposition.Redeliver"/>: schedule a copy on the host's queue at now plus the
///   delay (body, message id, content type and every property, with the redelivery count set), then
///   complete the original. Scheduling first means a crash in between yields a duplicate the inbox
///   absorbs, never a lost message.</item>
///   <item><see cref="BrokerDisposition.DeadLetter"/>: move the message to the queue's native
///   dead-letter queue with the reason and the detail.</item>
///   <item>The host stopping (the pipeline's <see cref="OperationCanceledException"/>): abandon the
///   message, best effort, so it is delivered again after the restart.</item>
/// </list>
/// A message the pipeline has finished with is settled with no cancellation token: the work is done,
/// and settling it during shutdown is what keeps it from running twice.
/// </summary>
internal sealed partial class AzureServiceBusDeliveryHandler
{
    /// <summary>The longest dead-letter reason or description the Service Bus SDK accepts.</summary>
    internal const int MaxDeadLetterTextLength = 4096;

    private readonly IBrokerReceivePipeline _pipeline;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="AzureServiceBusDeliveryHandler"/> class.</summary>
    /// <param name="pipeline">The framework's receive pipeline.</param>
    /// <param name="timeProvider">The clock a redelivery is scheduled from.</param>
    /// <param name="logger">The logger a failed abandon is reported to.</param>
    public AzureServiceBusDeliveryHandler(IBrokerReceivePipeline pipeline, TimeProvider timeProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _pipeline = pipeline;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Processes and settles one delivered message.</summary>
    /// <param name="message">The delivered message.</param>
    /// <param name="settlement">The calls that settle it.</param>
    /// <param name="cancellationToken">Fires when the host is stopping.</param>
    /// <returns>A task that completes when the message is settled.</returns>
    public async Task HandleAsync(ServiceBusReceivedMessage message, IAzureServiceBusSettlement settlement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(settlement);

        BrokerDisposition disposition;
        try
        {
            disposition = await _pipeline.ProcessAsync(AzureServiceBusMessageMapping.ToIncoming(message), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AbandonBestEffortAsync(message, settlement).ConfigureAwait(false);
            return;
        }

        switch (disposition)
        {
            case BrokerDisposition.Completed:
                await settlement.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
                break;

            case BrokerDisposition.Redeliver redeliver:
                await settlement.ScheduleOnQueueAsync(
                    AzureServiceBusMessageMapping.CreateRedelivery(message, redeliver.RedeliveryCount),
                    _timeProvider.GetUtcNow() + redeliver.Delay,
                    CancellationToken.None).ConfigureAwait(false);
                await settlement.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
                break;

            case BrokerDisposition.DeadLetter deadLetter:
                await settlement.DeadLetterAsync(
                    Truncate(deadLetter.Reason) ?? string.Empty,
                    Truncate(deadLetter.Detail),
                    CancellationToken.None).ConfigureAwait(false);
                break;

            default:
                // A disposition this transport does not know is a framework bug: throwing lets the
                // processor abandon the message and report the error rather than guess.
                throw new InvalidOperationException($"Unknown broker disposition '{disposition.GetType().Name}'.");
        }
    }

    /// <summary>Cuts a dead-letter text to the length the SDK accepts.</summary>
    /// <param name="value">The text, or <see langword="null"/>.</param>
    /// <returns>The text, at most <see cref="MaxDeadLetterTextLength"/> characters.</returns>
    internal static string? Truncate(string? value) =>
        value is { Length: > MaxDeadLetterTextLength } ? value[..MaxDeadLetterTextLength] : value;

    private async Task AbandonBestEffortAsync(ServiceBusReceivedMessage message, IAzureServiceBusSettlement settlement)
    {
        try
        {
            await settlement.AbandonAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort during shutdown: an unabandoned message is redelivered once its lock expires anyway.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAbandonFailed(_logger, ex, message.MessageId);
        }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Could not abandon Azure Service Bus message {MessageId} while the host was stopping; it is delivered again when its lock expires.")]
    private static partial void LogAbandonFailed(ILogger logger, Exception exception, string? messageId);
}
