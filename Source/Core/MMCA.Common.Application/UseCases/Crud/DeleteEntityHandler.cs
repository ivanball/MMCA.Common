using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases.Crud;

/// <summary>
/// Generic delete handler that works for any aggregate root entity. Retrieves the entity
/// by ID, invokes its <c>Delete()</c> method (which may enforce business rules and raise
/// domain events), and persists the change if successful.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="UpdateEntityHandler{TEntity, TEntityDTO, TIdentifierType, TUpdateRequest}"/> the
/// handler is left unsealed and its workflow is split into overridable steps, because the two things
/// a real delete outgrows are both structural rather than behavioral: the child collections the
/// aggregate's own <c>Delete()</c> cascade has to see (declare them in <see cref="Includes"/>), and a
/// cross-aggregate invariant that must refuse the delete before it happens (implement
/// <see cref="OnDeletingAsync"/>). A subclass that overrides neither behaves exactly like this
/// handler, down to the query it issues.
/// </para>
/// <para>
/// <b>No events are raised here.</b> Domain events belong to the aggregate's <c>Delete()</c>, which
/// is what keeps the generic path and a hand-written one indistinguishable from the outside.
/// </para>
/// <para>
/// The framework's naming fitness rules (handlers end in <c>Handler</c> and are sealed) and the
/// vertical-slice co-location rule are scoped to a repo's own module assemblies, so this
/// framework-owned, deliberately unsealed generic is outside them; a consumer's subclass of it is a
/// normal module handler and must be sealed and co-located like any other.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The aggregate root entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public class DeleteEntityHandler<TEntity, TIdentifierType>(
    IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteEntityCommand<TEntity, TIdentifierType>, Result>
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>The ambient unit of work (exposed so an override can reach another repository).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>
    /// The name reported as the <c>Source</c> of a <c>NotFound</c> failure. Defaults to the open
    /// handler name rather than the runtime <c>`2</c>-suffixed one, so the failure reads the same
    /// whichever subclass produced it.
    /// </summary>
    protected virtual string HandlerName => nameof(DeleteEntityHandler<,>);

    /// <summary>
    /// Navigation properties to eager-load with the aggregate. Empty by default, which issues the
    /// same by-id query this handler has always issued. Override with the child collections the
    /// aggregate's <c>Delete()</c> cascade soft-deletes, or that a pre-delete invariant has to count:
    /// an unloaded collection leaves its rows live under a soft-deleted parent.
    /// </summary>
    protected virtual IEnumerable<string> Includes => [];

    /// <summary>
    /// Whether the aggregate is loaded with change tracking. <see langword="true"/> by default: a
    /// no-tracking load would turn the delete into a silent no-op.
    /// </summary>
    protected virtual bool AsTracking => true;

    /// <inheritdoc />
    public virtual async Task<Result> HandleAsync(
        DeleteEntityCommand<TEntity, TIdentifierType> command,
        CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetRepository<TEntity, TIdentifierType>();
        var entity = await LoadAsync(repository, command, cancellationToken).ConfigureAwait(false);
        if (entity is null)
            return Result.Failure(Error.NotFound.WithSource(HandlerName).WithTarget(typeof(TEntity).Name));

        var refusal = await OnDeletingAsync(entity, command, cancellationToken).ConfigureAwait(false);
        if (refusal.IsFailure)
            return refusal;

        var result = entity.Delete();
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            LogDeleted(entity, command);
        }

        return result;
    }

    /// <summary>
    /// Loads the aggregate. The default is a by-id load: with no <see cref="Includes"/> it issues
    /// the bare by-id query, and with includes the eager-loading overload under
    /// <see cref="AsTracking"/>. Override only when the command can address the aggregate some other
    /// way.
    /// </summary>
    /// <param name="repository">The aggregate's repository, already resolved from the unit of work.</param>
    /// <param name="command">The command being handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregate, or <see langword="null"/> when it does not exist.</returns>
    protected virtual Task<TEntity?> LoadAsync(
        IRepository<TEntity, TIdentifierType> repository,
        DeleteEntityCommand<TEntity, TIdentifierType> command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(command);

        var includes = Includes as IReadOnlyCollection<string> ?? [.. Includes];

        return includes.Count == 0
            ? repository.GetByIdAsync(command.Id, cancellationToken)
            : repository.GetByIdAsync(command.Id, includes, AsTracking, cancellationToken);
    }

    /// <summary>
    /// Pre-delete validation, run after the aggregate is loaded and before its <c>Delete()</c> is
    /// called. The default accepts. Override it with an invariant the aggregate itself cannot check
    /// because it spans more than the aggregate (a category that still has products, a parent whose
    /// subtree would be orphaned); a failure returns to the caller unchanged and nothing is saved.
    /// </summary>
    /// <param name="entity">The loaded, tracked aggregate.</param>
    /// <param name="command">The command being handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success to proceed with the delete, or the refusal that stops it.</returns>
    protected virtual Task<Result> OnDeletingAsync(
        TEntity entity,
        DeleteEntityCommand<TEntity, TIdentifierType> command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    /// <summary>
    /// Structured-logging hook, called after a successful save. The default does nothing; override it
    /// with a call to the module's own <c>[LoggerMessage]</c> partial.
    /// </summary>
    /// <param name="entity">The deleted aggregate.</param>
    /// <param name="command">The command that was handled.</param>
    protected virtual void LogDeleted(TEntity entity, DeleteEntityCommand<TEntity, TIdentifierType> command)
    {
        // No-op by design: logging is per-module vocabulary, so the base only provides the call site.
    }
}
