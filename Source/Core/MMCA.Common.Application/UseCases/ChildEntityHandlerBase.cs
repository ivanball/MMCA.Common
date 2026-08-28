using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases;

/// <summary>
/// The shared add-a-child-to-an-aggregate workflow: load the parent aggregate tracked and with its
/// child collection included, fail with <c>NotFound</c> when it is gone, delegate to the aggregate
/// method that owns the invariant, save only on success, and answer with the new child's DTO.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Includes"/> is <b>abstract on purpose</b>. Loading the child collection is what makes
/// the aggregate's duplicate check meaningful, so naming it has to be a deliberate act rather than an
/// inherited default; an add whose aggregate genuinely reads no existing child returns an empty list
/// and says so.
/// </para>
/// <para>
/// The child is mapped through <see cref="MapChild"/> rather than an injected
/// <c>IEntityDTOMapper</c>, because the DTO belongs to the CHILD entity, whose identifier type is
/// usually not the parent's. Implement it as a one-line call into the module's own child mapper.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TParent">The parent aggregate root.</typeparam>
/// <typeparam name="TIdentifierType">The parent aggregate's primary key type.</typeparam>
/// <typeparam name="TChild">The child entity the aggregate method returns.</typeparam>
/// <typeparam name="TChildDTO">The DTO returned on success.</typeparam>
public abstract class AddChildEntityHandlerBase<TCommand, TParent, TIdentifierType, TChild, TChildDTO>(
    IUnitOfWork unitOfWork)
    : ICommandHandler<TCommand, Result<TChildDTO>>
    where TParent : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>The ambient unit of work (exposed so an override can reach another repository).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>
    /// The name reported as the <c>Source</c> of a <c>NotFound</c> failure. Defaults to the concrete
    /// handler's own type name, which is what every hand-written copy passed.
    /// </summary>
    protected virtual string HandlerName => GetType().Name;

    /// <summary>
    /// Whether the parent aggregate is loaded with change tracking. <see langword="true"/> by
    /// default: a no-tracking load would turn the add into a silent no-op.
    /// </summary>
    protected virtual bool AsTracking => true;

    /// <summary>
    /// The child collection to eager-load with the parent. See the type remarks: this is abstract so
    /// that naming (or deliberately not naming) the join collection is an explicit decision.
    /// </summary>
    protected abstract IEnumerable<string> Includes { get; }

    /// <summary>Extracts the parent aggregate's primary key from the command.</summary>
    /// <param name="command">The command being handled.</param>
    /// <returns>The primary key to load.</returns>
    protected abstract TIdentifierType ParentId(TCommand command);

    /// <summary>
    /// Calls the aggregate method that adds the child and owns the invariant (uniqueness, capacity,
    /// state). A failure short-circuits before the save.
    /// </summary>
    /// <param name="parent">The loaded, tracked parent aggregate.</param>
    /// <param name="command">The command being handled.</param>
    /// <returns>The newly added child, or the refused invariant.</returns>
    protected abstract Result<TChild> Apply(TParent parent, TCommand command);

    /// <summary>Maps the newly added child to the DTO the handler answers with.</summary>
    /// <param name="child">The child returned by <see cref="Apply"/>.</param>
    /// <returns>The child's DTO.</returns>
    protected abstract TChildDTO MapChild(TChild child);

    /// <summary>
    /// Structured-logging hook, called after a successful save. The default does nothing; override it
    /// with a call to the module's own <c>[LoggerMessage]</c> partial.
    /// </summary>
    /// <param name="parent">The parent aggregate.</param>
    /// <param name="child">The newly added child.</param>
    /// <param name="command">The command that was handled.</param>
    protected virtual void LogAdded(TParent parent, TChild child, TCommand command)
    {
        // No-op by design: logging is per-module vocabulary, so the base only provides the call site.
    }

    /// <summary>
    /// Post-commit hook, called after <see cref="LogAdded"/> and before the DTO is mapped. The default
    /// does nothing.
    /// </summary>
    /// <param name="parent">The parent aggregate.</param>
    /// <param name="child">The newly added child.</param>
    /// <param name="command">The command that was handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected virtual Task OnAddedAsync(TParent parent, TChild child, TCommand command, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public virtual async Task<Result<TChildDTO>> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetRepository<TParent, TIdentifierType>();

        // The join collection has to be loaded, or the aggregate's duplicate check runs against an
        // empty in-memory list and the double-submit surfaces as a raw unique-index 409.
        var parent = await repository
            .GetByIdAsync(ParentId(command), Includes, AsTracking, cancellationToken)
            .ConfigureAwait(false);
        if (parent is null)
            return Result.Failure<TChildDTO>(Error.NotFound.WithSource(HandlerName).WithTarget(typeof(TParent).Name));

        var result = Apply(parent, command);
        if (result.IsFailure)
            return Result.Failure<TChildDTO>(result.Errors);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var child = result.Value!;

        LogAdded(parent, child, command);
        await OnAddedAsync(parent, child, command, cancellationToken).ConfigureAwait(false);

        return Result.Success(MapChild(child));
    }
}

/// <summary>
/// The shared remove-a-child-from-an-aggregate workflow: the load-mutate-save pipeline of
/// <see cref="MutateEntityHandlerBase{TCommand, TEntity, TIdentifierType}"/> with the child
/// collection made a required include, because a remove that cannot see the collection cannot find
/// the child and reports a wrong <c>NotFound</c>.
/// </summary>
/// <remarks>
/// Implement <c>MutateAsync</c> as a one-line call into the aggregate's remove method, wrapped with
/// <see cref="Task.FromResult{TResult}(TResult)"/>. A remove whose command can also arrive addressed
/// by the child's own id (with no parent id) overrides <c>LoadAsync</c> to resolve the owning root.
/// </remarks>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TParent">The parent aggregate root.</typeparam>
/// <typeparam name="TIdentifierType">The parent aggregate's primary key type.</typeparam>
public abstract class RemoveChildEntityHandlerBase<TCommand, TParent, TIdentifierType>(IUnitOfWork unitOfWork)
    : MutateEntityHandlerBase<TCommand, TParent, TIdentifierType>(unitOfWork)
    where TParent : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>
    /// The child collection to eager-load with the parent. Required here: the aggregate's remove
    /// method searches this collection.
    /// </summary>
    protected abstract override IEnumerable<string> Includes { get; }
}
