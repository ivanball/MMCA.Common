using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.UseCases;

/// <summary>
/// The shared load-mutate-save machinery behind every aggregate write handler: resolve the
/// repository, load the aggregate (tracked, with the includes the mutation needs), fail with
/// <c>NotFound</c> when it is gone, stamp the caller's optimistic-concurrency token, run the domain
/// mutation, and save only when the mutation succeeded.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately does <b>not</b> implement <see cref="ICommandHandler{TCommand, TResult}"/>.
/// The two shapes a real handler returns (a bare <see cref="Result"/> for verb-style commands like
/// publish/close/remove, and a <see cref="Result{T}"/> carrying the refreshed DTO for update-style
/// commands) are supplied by the two thin subclasses
/// <see cref="MutateEntityHandlerBase{TCommand, TEntity, TIdentifierType}"/> and
/// <see cref="MutateEntityHandlerBase{TCommand, TEntity, TIdentifierType, TEntityDTO}"/>. Keeping the
/// machinery here means a single handler type never advertises both handler interfaces, which would
/// otherwise register a bogus second handler entry during the module scan.
/// </para>
/// <para>
/// The repository is obtained from <see cref="IUnitOfWork"/>, never constructor-injected: only the
/// unit of work knows which physical data source the aggregate resolves to.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TEntity">The aggregate root being mutated.</typeparam>
/// <typeparam name="TIdentifierType">The aggregate's primary key type.</typeparam>
public abstract class MutateEntityHandlerCore<TCommand, TEntity, TIdentifierType>(IUnitOfWork unitOfWork)
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
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
    /// Navigation properties to eager-load with the aggregate. Empty by default, which is the same
    /// query the by-id-only overload issues. Override with the collections the mutation reads, for
    /// example the child collection a remove or a duplicate check has to see.
    /// </summary>
    protected virtual IEnumerable<string> Includes => [];

    /// <summary>
    /// Whether the aggregate is loaded with change tracking. <see langword="true"/> by default: a
    /// no-tracking load would turn the mutation into a silent no-op.
    /// </summary>
    protected virtual bool AsTracking => true;

    /// <summary>Extracts the aggregate's primary key from the command.</summary>
    /// <param name="command">The command being handled.</param>
    /// <returns>The primary key to load.</returns>
    protected abstract TIdentifierType EntityId(TCommand command);

    /// <summary>
    /// Extracts the caller's last-observed <c>RowVersion</c> from the command. Returns
    /// <see langword="null"/> by default, which skips the conflict check; override on any command
    /// whose request round-trips a concurrency token.
    /// </summary>
    /// <param name="command">The command being handled.</param>
    /// <returns>The client's last-observed row version, or <see langword="null"/> to skip the check.</returns>
    protected virtual byte[]? RowVersion(TCommand command) => null;

    /// <summary>
    /// Runs the domain mutation on the loaded aggregate. A failure short-circuits before the save, so
    /// a refused invariant never writes. A purely synchronous domain call is returned with
    /// <see cref="Task.FromResult{TResult}(TResult)"/>; the signature is asynchronous so a mutation
    /// that first has to consult another service (a cross-service lookup, a rights check) fits here
    /// too, with the aggregate already loaded and its concurrency token already stamped.
    /// </summary>
    /// <param name="entity">The loaded, tracked aggregate.</param>
    /// <param name="command">The command being handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mutation's result.</returns>
    protected abstract Task<Result> MutateAsync(TEntity entity, TCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the aggregate. The default is a by-id load using <see cref="Includes"/> and
    /// <see cref="AsTracking"/>. Override only when the command can address the aggregate some other
    /// way, for example resolving the owning root from a join-entity id.
    /// </summary>
    /// <param name="repository">The aggregate's repository, already resolved from the unit of work.</param>
    /// <param name="command">The command being handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregate, or <see langword="null"/> when it does not exist.</returns>
    protected virtual Task<TEntity?> LoadAsync(
        IRepository<TEntity, TIdentifierType> repository,
        TCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);

        return repository.GetByIdAsync(EntityId(command), Includes, AsTracking, cancellationToken);
    }

    /// <summary>
    /// Structured-logging hook, called after a successful save. The default does nothing; override it
    /// with a call to the module's own <c>[LoggerMessage]</c> partial.
    /// </summary>
    /// <param name="entity">The mutated aggregate.</param>
    /// <param name="command">The command that was handled.</param>
    protected virtual void LogMutated(TEntity entity, TCommand command)
    {
        // No-op by design: logging is per-module vocabulary, so the base only provides the call site.
    }

    /// <summary>
    /// Post-commit hook, called after <see cref="LogMutated"/>. The default does nothing. Override it
    /// for best-effort work that must never fail the command, such as enqueuing a broadcast.
    /// </summary>
    /// <param name="entity">The mutated aggregate.</param>
    /// <param name="command">The command that was handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected virtual Task OnMutatedAsync(TEntity entity, TCommand command, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Runs the whole load-mutate-save workflow and returns the mutated aggregate, so a subclass can
    /// shape the handler's own return value from it.
    /// </summary>
    /// <param name="command">The command being handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mutated aggregate, or the failure that stopped the write.</returns>
    protected async Task<Result<TEntity>> MutateCoreAsync(TCommand command, CancellationToken cancellationToken)
    {
        var repository = unitOfWork.GetRepository<TEntity, TIdentifierType>();
        var entity = await LoadAsync(repository, command, cancellationToken).ConfigureAwait(false);
        if (entity is null)
            return Result.Failure<TEntity>(Error.NotFound.WithSource(HandlerName).WithTarget(typeof(TEntity).Name));

        // ADR-035 optimistic concurrency: stamp the client's last-seen rowversion back as the
        // original, so an edit decided against a stale view fails the save with a
        // DbUpdateConcurrencyException (mapped to 409 Conflict) instead of silent last-write-wins.
        // Null skips the check.
        repository.SetOriginalRowVersion(entity, RowVersion(command));

        var result = await MutateAsync(entity, command, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
            return Result.Failure<TEntity>(result.Errors);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogMutated(entity, command);
        await OnMutatedAsync(entity, command, cancellationToken).ConfigureAwait(false);

        return Result.Success(entity);
    }
}

/// <summary>
/// A write handler over an existing aggregate that answers with a bare <see cref="Result"/>: the
/// verb-style commands (publish, unpublish, open, close, moderate, rename, remove a child) where the
/// caller needs only success or the refused invariant.
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TEntity">The aggregate root being mutated.</typeparam>
/// <typeparam name="TIdentifierType">The aggregate's primary key type.</typeparam>
public abstract class MutateEntityHandlerBase<TCommand, TEntity, TIdentifierType>(IUnitOfWork unitOfWork)
    : MutateEntityHandlerCore<TCommand, TEntity, TIdentifierType>(unitOfWork),
      ICommandHandler<TCommand, Result>
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public virtual async Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        var result = await MutateCoreAsync(command, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? Result.Failure(result.Errors) : Result.Success();
    }
}

/// <summary>
/// A write handler over an existing aggregate that answers with the refreshed DTO: the update-style
/// commands whose caller re-renders the aggregate from the response.
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TEntity">The aggregate root being mutated.</typeparam>
/// <typeparam name="TIdentifierType">The aggregate's primary key type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO returned on success.</typeparam>
public abstract class MutateEntityHandlerBase<TCommand, TEntity, TIdentifierType, TEntityDTO>(
    IUnitOfWork unitOfWork,
    IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType> dtoMapper)
    : MutateEntityHandlerCore<TCommand, TEntity, TIdentifierType>(unitOfWork),
      ICommandHandler<TCommand, Result<TEntityDTO>>
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
    where TEntityDTO : IBaseDTO<TIdentifierType>
{
    /// <inheritdoc />
    public virtual async Task<Result<TEntityDTO>> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        var result = await MutateCoreAsync(command, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? Result.Failure<TEntityDTO>(result.Errors)
            : Result.Success(dtoMapper.MapToDTO(result.Value!));
    }
}
