using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Abstraction for publishing integration events to an event bus.
/// The default implementation dispatches in-process via <see cref="IDomainEventDispatcher"/>
/// with outbox persistence for at-least-once delivery. Alternative implementations
/// (e.g., Azure Service Bus, RabbitMQ) can be substituted via DI.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Publishes an integration event to all registered handlers.
    /// </summary>
    /// <param name="integrationEvent">The integration event to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes multiple integration events to all registered handlers.
    /// </summary>
    /// <param name="integrationEvents">The integration events to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAsync(IEnumerable<IIntegrationEvent> integrationEvents, CancellationToken cancellationToken = default);
}
