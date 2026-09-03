using MassTransit;
using Microsoft.Extensions.Logging;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// Consumes the <see cref="Fault{TEvent}"/> message MassTransit publishes when a consumer for
/// <typeparamref name="TEvent"/> exhausts its retry policy, turning what would otherwise be a
/// silent row in the broker's <c>_error</c> queue into one structured Error log plus a
/// <c>broker.fault.count</c> metric tagged by event type.
/// <para>
/// Registered automatically alongside every consumer wired through
/// <c>RegisterIntegrationEventConsumer&lt;TEvent&gt;</c> (opt out per event with its
/// <c>registerFaultConsumer</c> parameter).
/// </para>
/// <para>
/// This consumer never throws. A fault consumer that faults would publish
/// <c>Fault&lt;Fault&lt;TEvent&gt;&gt;</c> and, on a broker with second-level redelivery enabled,
/// keep re-entering itself: observability code must not be able to create its own incident. The
/// original message is already in the error queue and is not replayed from here.
/// </para>
/// </summary>
/// <typeparam name="TEvent">The integration event type whose faults are observed.</typeparam>
/// <param name="logger">Logger for the fault diagnostic.</param>
public sealed partial class FaultIntegrationEventConsumer<TEvent>(
    ILogger<FaultIntegrationEventConsumer<TEvent>> logger) : IConsumer<Fault<TEvent>>
    where TEvent : class, IIntegrationEvent
{
    /// <inheritdoc />
    public Task Consume(ConsumeContext<Fault<TEvent>> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Fault<TEvent> fault = context.Message;

        // The broker's own message id when MassTransit captured one, otherwise the fault id: both
        // are what an operator pastes into a queue browser, and one of them is always present.
        var messageId = fault.FaultedMessageId ?? fault.FaultId;

        // Every exception in the chain, innermost cause included, on one line: the stack traces
        // stay in the error queue's message headers, and the message text is what identifies the
        // failure at a glance.
        var reasons = fault.Exceptions is { Length: > 0 } exceptions
            ? string.Join(" | ", exceptions.Select(e => e.Message))
            : "<no exception detail>";

        LogFault(logger, messageId, typeof(TEvent).Name, reasons);

        BrokerMetrics.FaultCounter.Add(
            1,
            new KeyValuePair<string, object?>("event_type", typeof(TEvent).Name));

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Integration event {EventType} (message {MessageId}) faulted after exhausting its retry policy and was moved to the error queue: {Reasons}")]
    private static partial void LogFault(ILogger logger, Guid messageId, string eventType, string reasons);
}
