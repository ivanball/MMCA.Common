using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Messaging;

/// <summary>
/// Abstraction for publishing integration events across module or service boundaries.
/// Application code that needs to publish cross-cutting events should depend on this
/// interface rather than on <see cref="MMCA.Common.Application.Interfaces.IEventBus"/>
/// or on a transport-specific client.
/// <para>
/// Two implementations are provided in <c>MMCA.Common.Infrastructure</c>:
/// <list type="bullet">
///   <item>
///     <c>InProcessMessageBus</c> — dispatches synchronously through the existing
///     <see cref="MMCA.Common.Application.Interfaces.IDomainEventDispatcher"/> path.
///     Used by the modular monolith deployment.
///   </item>
///   <item>
///     <c>BrokerMessageBus</c> — publishes via MassTransit to an external broker
///     (RabbitMQ in development, Azure Service Bus in production). Used by extracted
///     microservices. The transactional outbox semantics are preserved by
///     <c>OutboxProcessor</c>, which drains <c>OutboxMessage</c> entries through
///     this bus instead of dispatching in-process.
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
