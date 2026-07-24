namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// Marker and contract for aggregate roots in the DDD sense. Aggregates are the only
/// entities that can raise domain events, and they define the transactional consistency boundary.
/// The infrastructure layer (<c>ApplicationDbContext</c>) uses this interface to discover
/// pending domain events across all tracked aggregates during <c>SaveChangesAsync</c>.
/// </summary>
public interface IAggregateRoot
{
    /// <summary>Gets the domain events raised during the current business operation.</summary>
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    /// <summary>Enqueues a domain event for post-persistence dispatch.</summary>
    /// <param name="domainEvent">The domain event to add.</param>
    void AddDomainEvent(IDomainEvent domainEvent);

    /// <summary>Clears all pending domain events after they have been dispatched.</summary>
    void ClearDomainEvents();

    /// <summary>
    /// Removes only the specified events, leaving any raised since they were captured.
    /// </summary>
    /// <param name="domainEvents">The events to remove.</param>
    /// <remarks>
    /// The persistence pipeline captures an aggregate's events before saving and clears them
    /// afterwards. Clearing wholesale discards anything a handler raised on the same aggregate
    /// during in-process dispatch, because those events arrive after the capture and are wiped
    /// before any later capture can see them: they never dispatch and never reach the outbox.
    /// Removing exactly what was captured leaves them pending for the next save instead.
    /// </remarks>
    void RemoveDomainEvents(IEnumerable<IDomainEvent> domainEvents);
}
