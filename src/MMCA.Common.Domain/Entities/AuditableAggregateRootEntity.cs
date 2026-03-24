using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Entities;

/// <summary>
/// Base class for aggregate roots — entities that own a consistency boundary and
/// can raise domain events. Domain events are collected during the business operation
/// and dispatched by <c>ApplicationDbContext.SaveChangesAsync</c> after successful
/// persistence, then cleared. This ensures events are never dispatched for failed saves.
/// </summary>
/// <typeparam name="TIdentifierType">The aggregate's identifier type.</typeparam>
public abstract class AuditableAggregateRootEntity<TIdentifierType> : AuditableBaseEntity<TIdentifierType>, IAggregateRoot
    where TIdentifierType : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>
    /// Enqueues a domain event to be dispatched after the next successful <c>SaveChangesAsync</c>.
    /// </summary>
    /// <param name="domainEvent">The domain event to enqueue.</param>
    public void AddDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Removes all pending domain events. Called by the infrastructure layer after
    /// events have been successfully dispatched to prevent duplicate delivery.
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>
    /// Replaces a child entity collection with a new set of items, invoking
    /// <see cref="ValidateSetItems{TChildEntity}"/> before mutation so aggregates can
    /// enforce business rules (e.g., preventing removal of shipped order lines).
    /// </summary>
    /// <typeparam name="TChildEntity">The child entity type within this aggregate.</typeparam>
    /// <param name="collection">The backing list (field) of the child collection.</param>
    /// <param name="items">The new items to replace the current collection with.</param>
    protected void SetItems<TChildEntity>(
        List<TChildEntity> collection,
        IEnumerable<TChildEntity> items)
        where TChildEntity : IAuditableEntity
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(items);

        // Materialize once to avoid multiple enumeration and allow validation to inspect the list.
        var itemsList = items as IList<TChildEntity> ?? [.. items];
        ValidateSetItems(collection, itemsList);

        collection.Clear();
        collection.AddRange(itemsList);
    }

    /// <summary>
    /// Hook for aggregates to validate incoming child items against the current collection
    /// before replacement occurs. Override this to enforce invariants such as preventing
    /// removal of items that have been fulfilled or restricting collection size.
    /// The default implementation performs no validation.
    /// </summary>
    /// <typeparam name="TChildEntity">The child entity type within this aggregate.</typeparam>
    /// <param name="currentItems">The current items in the collection (before replacement).</param>
    /// <param name="incomingItems">The proposed replacement items.</param>
    protected virtual void ValidateSetItems<TChildEntity>(
        IList<TChildEntity> currentItems,
        IList<TChildEntity> incomingItems)
        where TChildEntity : IAuditableEntity
    {
    }

    /// <summary>
    /// Searches a child entity collection for an active (non-deleted) item by ID.
    /// Returns a <see cref="Result{T}"/> with the item on success, or a
    /// <see cref="Error.NotFound"/> failure if the item does not exist or is soft-deleted.
    /// </summary>
    /// <typeparam name="TChild">The child entity type (must be auditable and have an identifier).</typeparam>
    /// <typeparam name="TChildId">The child entity's identifier type.</typeparam>
    /// <param name="collection">The backing list of child entities to search.</param>
    /// <param name="childId">The identifier of the child entity to find.</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <returns>The child entity wrapped in a success result, or a NotFound failure.</returns>
    protected static Result<TChild> GetChildOrNotFound<TChild, TChildId>(
        IEnumerable<TChild> collection,
        TChildId childId,
        string source)
        where TChild : AuditableBaseEntity<TChildId>
        where TChildId : notnull
    {
        var child = collection.FirstOrDefault(c => c.Id.Equals(childId) && !c.IsDeleted);
        if (child is null)
        {
            return Result.Failure<TChild>(
                Error.NotFound
                    .WithSource(source)
                    .WithTarget(typeof(TChild).Name));
        }

        return Result.Success(child);
    }
}
