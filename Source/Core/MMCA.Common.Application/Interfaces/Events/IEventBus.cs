using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Interfaces.Events;

/// <summary>
/// Abstraction for publishing integration events to an event bus.
/// The default implementation (<c>InProcessEventBus</c>) dispatches in-process via
/// <see cref="IDomainEventDispatcher"/>, persisting outbox rows first for at-least-once retry only
/// when <c>MessageBus:EnableOutbox</c> resolves to true (it is off by default for the in-process
/// provider). A broker <c>MessageBus:Provider</c> replaces it with <c>BrokerEventBus</c>, which writes
/// the outbox row for the outbox processor to publish.
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
