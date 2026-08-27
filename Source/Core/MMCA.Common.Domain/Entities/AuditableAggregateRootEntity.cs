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

    /// <inheritdoc />
    public void RemoveDomainEvents(IEnumerable<IDomainEvent> domainEvents)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        // Reference equality: two structurally equal events raised separately are still two
        // distinct occurrences, and only the captured instances have been delivered.
        var captured = new HashSet<IDomainEvent>(domainEvents, ReferenceEqualityComparer.Instance);
        if (captured.Count == 0)
        {
            return;
        }

        _domainEvents.RemoveAll(captured.Contains);
    }

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

    /// <summary>
    /// Soft-deletes one child of this aggregate, found by id: the
    /// <see cref="GetChildOrNotFound{TChild, TChildId}"/> lookup followed by the child's own
    /// <c>Delete()</c>, short-circuiting on either failure with that step's errors. The NotFound
    /// failure is byte-identical to the one <see cref="GetChildOrNotFound{TChild, TChildId}"/>
    /// produces, so replacing a hand-written get-then-delete pair with this helper is
    /// behavior-preserving.
    /// <para>
    /// The deleted child comes back in the result rather than being consumed here, because the
    /// domain event a removal raises is aggregate vocabulary: which event, and what it carries, is
    /// the CALLER's decision. The helper owns the mechanics, the aggregate method owns the meaning:
    /// <code>
    /// public Result RemoveOrderLine(OrderLineIdentifierType lineId)
    /// {
    ///     var result = RemoveChildOrNotFound&lt;OrderLine, OrderLineIdentifierType&gt;(
    ///         _lines, lineId, nameof(RemoveOrderLine));
    ///     if (result.IsFailure)
    ///         return Result.Failure(result.Errors);
    ///
    ///     AddDomainEvent(new OrderLineChanged(DomainEntityState.Deleted, Id, result.Value!.Id));
    ///     return Result.Success();
    /// }
    /// </code>
    /// </para>
    /// </summary>
    /// <typeparam name="TChild">The child entity type (must be auditable and have an identifier).</typeparam>
    /// <typeparam name="TChildId">The child entity's identifier type.</typeparam>
    /// <param name="collection">The backing list of child entities to search.</param>
    /// <param name="childId">The identifier of the child entity to remove.</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <returns>
    /// The soft-deleted child on success, a NotFound failure when no ACTIVE child carries that id,
    /// or the child's own delete failure.
    /// </returns>
    protected static Result<TChild> RemoveChildOrNotFound<TChild, TChildId>(
        IEnumerable<TChild> collection,
        TChildId childId,
        string source)
        where TChild : AuditableBaseEntity<TChildId>
        where TChildId : notnull
    {
        var childResult = GetChildOrNotFound<TChild, TChildId>(collection, childId, source);
        if (childResult.IsFailure)
        {
            return childResult;
        }

        var child = childResult.Value!;

        var deleteResult = child.Delete();
        if (deleteResult.IsFailure)
        {
            return Result.Failure<TChild>(deleteResult.Errors);
        }

        return Result.Success(child);
    }

    /// <summary>
    /// Brings a soft-deleted child back into this aggregate's visible set (BR-135): the candidate is
    /// reactivated and re-added to the collection when it is not already there.
    /// <para>
    /// The child is taken as an INSTANCE rather than an id, because a soft-deleted row is excluded
    /// by the global query filter and is therefore not reachable through the loaded collection: the
    /// caller resolves it with an <c>ignoreQueryFilters</c> read and hands it in. As with
    /// <see cref="RemoveChildOrNotFound{TChild, TChildId}"/>, the restored child comes back in the
    /// result so the caller raises its own domain event.
    /// </para>
    /// <para>
    /// Only the "not soft-deleted" rule lives here. Ownership checks (does this child belong to this
    /// aggregate) and re-validation of organizer-entered fields are aggregate-specific and stay in
    /// the calling method, which runs them BEFORE calling this helper so a rejected restore leaves
    /// the child untouched and still deleted.
    /// </para>
    /// </summary>
    /// <typeparam name="TChild">The child entity type (must be auditable, identified, and reactivatable).</typeparam>
    /// <typeparam name="TChildId">The child entity's identifier type.</typeparam>
    /// <param name="collection">The backing list of child entities to restore into.</param>
    /// <param name="child">The soft-deleted child to restore.</param>
    /// <param name="notDeletedErrorCode">
    /// The aggregate's own error code for "this candidate is not soft-deleted" (for example
    /// <c>"Event.Room.NotDeleted"</c>). Taken as a parameter for the same reason
    /// <see cref="GetChildOrNotFound{TChild, TChildId}"/> takes <paramref name="source"/>: the code
    /// is consumer vocabulary the framework must not invent.
    /// </param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <returns>
    /// The reactivated child on success, an invariant failure when it is not soft-deleted, or the
    /// child's own reactivation failure.
    /// </returns>
    protected static Result<TChild> RestoreChild<TChild, TChildId>(
        List<TChild> collection,
        TChild child,
        string notDeletedErrorCode,
        string source)
        where TChild : AuditableBaseEntity<TChildId>, IReactivatable
        where TChildId : notnull
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(child);

        if (!child.IsDeleted)
        {
            return Result.Failure<TChild>(
                Error.Invariant(
                    code: notDeletedErrorCode,
                    message: $"Cannot restore a {typeof(TChild).Name} that is not soft-deleted.",
                    source: source,
                    target: typeof(TChild).Name));
        }

        var reactivateResult = child.Reactivate();
        if (reactivateResult.IsFailure)
        {
            return Result.Failure<TChild>(reactivateResult.Errors);
        }

        // A caller that resolved the child through an ignoreQueryFilters read holds an instance the
        // loaded collection never contained, so the re-add is what puts it back in the aggregate.
        // A caller whose collection already carries it (an ignoreQueryFilters read THROUGH this
        // aggregate) must not get a duplicate.
        if (!collection.Exists(existing => existing.Id.Equals(child.Id)))
        {
            collection.Add(child);
        }

        return Result.Success(child);
    }

    /// <summary>
    /// Cascades a soft-delete across a child entity collection: every child that is still active is
    /// deleted, and the failures (if any) are aggregated into one result. Children that are already
    /// deleted are skipped rather than reported, so re-deleting a parent is idempotent with respect
    /// to its children and does not surface an <c>Error.AlreadyDeleted</c> the caller cannot act on.
    /// <para>
    /// This replaces the loop each aggregate used to hand-roll in its own <c>Delete()</c> override
    /// (iterate the children, call <c>Delete()</c>, collect the results, combine). Call it before
    /// deleting the root so a failing child aborts the whole cascade:
    /// <code>
    /// public override Result Delete() =>
    ///     Result.Combine(DeleteChildren&lt;OrderLine, OrderLineIdentifierType&gt;(_lines), base.Delete());
    /// </code>
    /// </para>
    /// </summary>
    /// <typeparam name="TChild">The child entity type (must be auditable and have an identifier).</typeparam>
    /// <typeparam name="TChildId">The child entity's identifier type.</typeparam>
    /// <param name="children">The child entities to soft-delete.</param>
    /// <returns>
    /// A success result when every active child was deleted (or the collection had none), otherwise
    /// a failure carrying every child's error.
    /// </returns>
    protected static Result DeleteChildren<TChild, TChildId>(IEnumerable<TChild> children)
        where TChild : AuditableBaseEntity<TChildId>
        where TChildId : notnull
    {
        ArgumentNullException.ThrowIfNull(children);

        List<Result>? results = null;

        foreach (var child in children)
        {
            if (child.IsDeleted)
            {
                continue;
            }

            (results ??= []).Add(child.Delete());
        }

        return results is null ? Result.Success() : Result.Combine([.. results]);
    }
}
