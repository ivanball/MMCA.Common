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
}
