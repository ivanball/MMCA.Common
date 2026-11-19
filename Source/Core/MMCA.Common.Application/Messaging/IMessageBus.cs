using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Messaging;

/// <summary>
/// The transport integration events are published through across module or service boundaries.
/// <c>OutboxProcessor</c> drains persisted outbox rows through it; it never writes the outbox
/// itself, so application code publishes through
/// <see cref="MMCA.Common.Application.Interfaces.Events.IEventBus"/>, which persists the outbox row,
/// rather than depending on this interface or on a transport-specific client.
/// <para>
/// Two implementations are provided in <c>MMCA.Common.Infrastructure</c>:
/// <list type="bullet">
///   <item>
///     <c>InProcessMessageBus</c> — dispatches synchronously through the existing
///     <see cref="MMCA.Common.Application.Interfaces.Events.IDomainEventDispatcher"/> path.
///     Used by the modular monolith deployment.
///   </item>
///   <item>
///     <c>BrokerMessageBus</c> — publishes via MassTransit to an external broker
///     (RabbitMQ in development, Azure Service Bus in production). Used by extracted
///     microservices. The transactional outbox semantics are preserved by
///     <c>OutboxProcessor</c>, which drains <c>OutboxMessage</c> entries through
///     whichever bus is registered, so with this one they reach the broker.
///   </item>
/// </list>
/// </para>
/// </summary>
public interface IMessageBus
{
    /// <summary>
    /// Publishes a single integration event.
    /// </summary>
    /// <param name="integrationEvent">The integration event to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a batch of integration events.
    /// </summary>
    /// <param name="integrationEvents">The integration events to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAsync(IEnumerable<IIntegrationEvent> integrationEvents, CancellationToken cancellationToken = default);
}
