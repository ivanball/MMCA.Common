using MassTransit;
using MassTransit.Metadata;
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
/// <para>
/// MassTransit publishes one <c>Fault&lt;TEvent&gt;</c> per faulting consumer to a topic every
/// service consuming <typeparamref name="TEvent"/> subscribes to, so a co-consuming service
/// receives the other services' faults too. Only a fault this host produced is logged at Error and
/// counted: the fault's <c>Host.Assembly</c> (the entry-assembly name MassTransit stamps; not the
/// process name, which is <c>dotnet</c> for every container, nor the machine name, which differs
/// between replicas of one service) is compared ordinally with this host's own. A fault from
/// another assembly gets one Debug line and nothing else; a fault with no host information, or
/// either side missing its assembly name, keeps the Error and the count (the conservative answer).
/// The counter carries the faulting assembly as a <c>faulting_assembly</c> tag.
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

        var faultingAssembly = fault.Host?.Assembly;
        var ownAssembly = HostMetadataCache.Host?.Assembly;
        if (!string.IsNullOrEmpty(faultingAssembly)
            && !string.IsNullOrEmpty(ownAssembly)
            && !string.Equals(faultingAssembly, ownAssembly, StringComparison.Ordinal))
        {
            LogForeignFault(logger, typeof(TEvent).Name, faultingAssembly);
            return Task.CompletedTask;
        }

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
            new KeyValuePair<string, object?>("event_type", typeof(TEvent).Name),
            new KeyValuePair<string, object?>("faulting_assembly", faultingAssembly));

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Integration event {EventType} (message {MessageId}) faulted after exhausting its retry policy and was moved to the error queue: {Reasons}")]
    private static partial void LogFault(ILogger logger, Guid messageId, string eventType, string reasons);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Integration event {EventType} fault produced by {Assembly}, observed here only because this host consumes the same event")]
    private static partial void LogForeignFault(ILogger logger, string eventType, string assembly);
}
