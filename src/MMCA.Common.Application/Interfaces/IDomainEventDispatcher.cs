using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Dispatches domain events to their registered handlers after an aggregate persists changes.
/// </summary>
public interface IDomainEventDispatcher
{
    /// <summary>
    /// Dispatches each domain event to all registered <see cref="IDomainEventHandler{T}"/> instances.
    /// </summary>
    /// <param name="domainEvents">The domain events to dispatch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when all handlers have finished.</returns>
    Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default);
}
