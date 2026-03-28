using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Publishes integration events for cross-module communication. Unlike domain events
/// (which are raised by aggregate roots via <c>AddDomainEvent()</c> and dispatched automatically
/// during <c>SaveChangesAsync</c>), integration events can be published explicitly from
/// command handlers, domain event handlers, or application services.
/// <para>
/// Published events are persisted to the outbox for at-least-once delivery and dispatched
/// to all registered <see cref="IIntegrationEventHandler{T}"/> implementations.
/// </para>
/// </summary>
public interface IIntegrationEventPublisher
{
    /// <summary>
    /// Publishes an integration event. The event is persisted to the outbox in the current
    /// unit of work transaction and dispatched in-process after successful persistence.
    /// </summary>
    /// <param name="integrationEvent">The integration event to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the event has been persisted and dispatched.</returns>
    Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}
