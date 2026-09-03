using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.UseCases.Crud;

/// <summary>
/// The shared load-mutate-save machinery behind every aggregate write handler: resolve the
/// repository, load the aggregate (tracked, with the includes the mutation needs), fail with
/// <c>NotFound</c> when it is gone, stamp the caller's optimistic-concurrency token, run the domain
/// mutation, and save only when the mutation succeeded.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately does <b>not</b> implement <see cref="ICommandHandler{TCommand, TResult}"/>.
/// The three shapes a real handler returns (a bare <see cref="Result"/> for verb-style commands like
/// publish/close/remove, a <see cref="Result{T}"/> carrying the refreshed DTO for update-style
/// commands, and a <see cref="Result{T}"/> carrying a payload the handler builds itself) are supplied
/// by the three thin subclasses
/// <see cref="MutateEntityHandlerBase{TCommand, TEntity, TIdentifierType}"/>,
/// <see cref="MutateEntityHandlerBase{TCommand, TEntity, TIdentifierType, TEntityDTO}"/> and
/// <see cref="MutateEntityPayloadHandlerBase{TCommand, TEntity, TIdentifierType, TResultPayload}"/>.
/// Keeping the machinery here means a single handler type never advertises two handler interfaces,
/// which would otherwise register a bogus second handler entry during the module scan.
/// </para>
/// <para>
/// The repository is obtained from <see cref="IUnitOfWork"/>, never constructor-injected: only the
/// unit of work knows which physical data source the aggregate resolves to.
/// </para>
/// <para>
/// <b>The mutation context.</b> Every run creates one <see cref="MutationContext"/> and threads it
/// through load, mutate and the post-save hooks. It carries values a mutation derived while the
/// aggregate was loaded out to the post-save hooks and to the result the handler builds, and it
/// carries the <see cref="MutationContext.SkipSave"/> short-circuit for the idempotent no-op case.
/// A handler that needs neither keeps overriding the context-free hooks and never sees it.
/// </para>
/// <para>
/// <b>Attempt scope.</b> <see cref="MutateCoreAsync(IUnitOfWork, TCommand, CancellationToken)"/>
/// takes the unit of work as a parameter, exactly like the create workflow's <c>CreateCoreAsync</c>,
/// so a handler whose write can lose a race (a unique-key collision on a child it adds) can override
/// <c>HandleAsync</c>, wrap the workflow in a retry loop and run each attempt against a fresh DI
/// scope's unit of work. The ambient context still tracks the failed attempt, so a retry on the
/// injected unit of work would never persist.
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
    /// <see langword="null"/> by default, for a mutation whose endpoint states no precondition at
    /// all; override on any command reached through a conditional (<c>If-Match</c>) endpoint, where
    /// the token is always present.
    /// </summary>
    /// <param name="command">The command being handled.</param>
    /// <returns>The client's last-observed row version, or <see langword="null"/> when the endpoint is unconditional.</returns>
    protected virtual byte[]? RowVersion(TCommand command) => null;

    /// <summary>
    /// Creates the <see cref="MutationContext"/> for one run of the workflow. The default is an
    /// empty context; override only to pre-seed values every hook of this handler expects to find.
    /// </summary>
    /// <returns>The context threaded through this run.</returns>
    protected virtual MutationContext CreateContext() => new();

    /// <summary>
    /// Runs the domain mutation on the loaded aggregate. A failure short-circuits before the save, so
    /// a refused invariant never writes. A purely synchronous domain call is returned with
    /// <see cref="Task.FromResult{TResult}(TResult)"/>; the signature is asynchronous so a mutation
    /// that first has to consult another service (a cross-service lookup, a rights check) fits here
    /// too, with the aggregate already loaded and its concurrency token already stamped.
    /// </summary>
    /// <remarks>
    /// A handler overrides <b>exactly one</b> of the two <c>MutateAsync</c> overloads: this one when
    /// the mutation needs nothing but the aggregate and the command, and
    /// <see cref="MutateAsync(TEntity, TCommand, MutationContext, CancellationToken)"/> when it has
    /// to carry a derived value forward or short-circuit the save. Overriding neither is a
    /// programming error and throws.
    /// </remarks>
    /// <param name="entity">The loaded, tracked aggregate.</param>
    /// <param name="command">The command being handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mutation's result.</returns>
    /// <exception cref="InvalidOperationException">Neither <c>MutateAsync</c> overload was overridden.</exception>
    protected virtual Task<Result> MutateAsync(TEntity entity, TCommand command, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            $"'{GetType().Name}' overrides neither MutateAsync overload. A mutate handler must override exactly one of them.");

    /// <summary>
    /// The context-aware mutation. Identical to
    /// <see cref="MutateAsync(TEntity, TCommand, CancellationToken)"/>, which it forwards to by
    /// default, except that it also receives this run's <see cref="MutationContext"/>: write the
    /// values the post-save hooks or the handler's own result will need into it while the aggregate
    /// is loaded, or call <see cref="MutationContext.SkipSave"/> to finish the command successfully
    /// without writing.
    /// </summary>
    /// <param name="entity">The loaded, tracked aggregate.</param>
    /// <param name="command">The command being handled.</param>
    /// <param name="context">This run's mutation context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mutation's result.</returns>
    protected virtual Task<Result> MutateAsync(
        TEntity entity,
        TCommand command,
        MutationContext context,
        CancellationToken cancellationToken) =>
        MutateAsync(entity, command, cancellationToken);

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
    /// The context-aware load, called by the workflow. Forwards to
    /// <see cref="LoadAsync(IRepository{TEntity, TIdentifierType}, TCommand, CancellationToken)"/> by
    /// default; override this overload instead when the load itself derives a value later hooks need.
    /// </summary>
    /// <param name="repository">The aggregate's repository, already resolved from the unit of work.</param>
    /// <param name="command">The command being handled.</param>
    /// <param name="context">This run's mutation context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregate, or <see langword="null"/> when it does not exist.</returns>
    protected virtual Task<TEntity?> LoadAsync(
        IRepository<TEntity, TIdentifierType> repository,
        TCommand command,
        MutationContext context,
        CancellationToken cancellationToken) =>
        LoadAsync(repository, command, cancellationToken);

    /// <summary>
    /// Structured-logging hook, called after a successful save. The default does nothing; override it
    /// with a call to the module's own <c>[LoggerMessage]</c> partial. Not called when the mutation
    /// short-circuited through <see cref="MutationContext.SkipSave"/>: nothing was written, so
    /// nothing should be logged as written.
    /// </summary>
    /// <param name="entity">The mutated aggregate.</param>
    /// <param name="command">The command that was handled.</param>
    protected virtual void LogMutated(TEntity entity, TCommand command)
    {
        // No-op by design: logging is per-module vocabulary, so the base only provides the call site.
    }

    /// <summary>
    /// The context-aware logging hook, called by the workflow. Forwards to
    /// <see cref="LogMutated(TEntity, TCommand)"/> by default; override this overload instead to log
    /// a value the mutation derived before it wrote.
    /// </summary>
    /// <param name="entity">The mutated aggregate.</param>
    /// <param name="command">The command that was handled.</param>
    /// <param name="context">This run's mutation context.</param>
    protected virtual void LogMutated(TEntity entity, TCommand command, MutationContext context) =>
        LogMutated(entity, command);

    /// <summary>
    /// Post-commit hook, called after <see cref="LogMutated(TEntity, TCommand, MutationContext)"/>.
    /// The default does nothing. Override it for best-effort work that must never fail the command,
    /// such as enqueuing a broadcast or deleting the blob the write just orphaned. Not called when
    /// the mutation short-circuited through <see cref="MutationContext.SkipSave"/>.
    /// </summary>
    /// <param name="entity">The mutated aggregate.</param>
    /// <param name="command">The command that was handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected virtual Task OnMutatedAsync(TEntity entity, TCommand command, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// The context-aware post-commit hook, called by the workflow. Forwards to
    /// <see cref="OnMutatedAsync(TEntity, TCommand, CancellationToken)"/> by default; override this
    /// overload instead to act on a value the mutation derived before it wrote, which is the shape of
    /// every "clean up what the write replaced" step.
    /// </summary>
    /// <param name="entity">The mutated aggregate.</param>
    /// <param name="command">The command that was handled.</param>
    /// <param name="context">This run's mutation context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected virtual Task OnMutatedAsync(
        TEntity entity,
        TCommand command,
        MutationContext context,
        CancellationToken cancellationToken) =>
        OnMutatedAsync(entity, command, cancellationToken);

    /// <summary>
    /// Runs the whole load-mutate-save workflow against the injected unit of work and returns the
    /// mutated aggregate, so a subclass can shape the handler's own return value from it.
    /// </summary>
    /// <param name="command">The command being handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mutated aggregate, or the failure that stopped the write.</returns>
    protected Task<Result<TEntity>> MutateCoreAsync(TCommand command, CancellationToken cancellationToken) =>
        MutateCoreAsync(unitOfWork, command, CreateContext(), cancellationToken);

    /// <summary>
    /// Runs one attempt of the whole workflow against <paramref name="attemptUnitOfWork"/>.
    /// </summary>
    /// <param name="attemptUnitOfWork">
    /// The unit of work this attempt runs against. Normally the injected one; a retrying subclass
    /// passes a fresh scope's unit of work, because the ambient DbContext still tracks the failed
    /// attempt and a clean context is required for the retry to persist.
    /// </param>
    /// <param name="command">The command being handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mutated aggregate, or the failure that stopped the write.</returns>
    protected Task<Result<TEntity>> MutateCoreAsync(
        IUnitOfWork attemptUnitOfWork,
        TCommand command,
        CancellationToken cancellationToken) =>
        MutateCoreAsync(attemptUnitOfWork, command, CreateContext(), cancellationToken);

    /// <summary>
    /// Runs one attempt of the whole workflow against <paramref name="attemptUnitOfWork"/> with a
    /// caller-supplied context, so the caller can read the side data the mutation wrote after the
    /// workflow returns.
    /// </summary>
    /// <param name="attemptUnitOfWork">The unit of work this attempt runs against.</param>
    /// <param name="command">The command being handled.</param>
    /// <param name="context">The context threaded through this run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mutated aggregate, or the failure that stopped the write.</returns>
    protected async Task<Result<TEntity>> MutateCoreAsync(
        IUnitOfWork attemptUnitOfWork,
        TCommand command,
        MutationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attemptUnitOfWork);
        ArgumentNullException.ThrowIfNull(context);

        var repository = attemptUnitOfWork.GetRepository<TEntity, TIdentifierType>();
        var entity = await LoadAsync(repository, command, context, cancellationToken).ConfigureAwait(false);
        if (entity is null)
            return Result.Failure<TEntity>(Error.NotFound.WithSource(HandlerName).WithTarget(typeof(TEntity).Name));

        // ADR-035 optimistic concurrency: stamp the client's last-seen rowversion back as the
        // original, so an edit decided against a stale view fails the save with a
        // DbUpdateConcurrencyException (mapped to 412 Precondition Failed) instead of silent
        // last-write-wins. A handler serving an unconditional endpoint reports no token and the
        // stamp is skipped; a conditional endpoint always has one, because a request without an
        // If-Match header never reaches the action.
        if (RowVersion(command) is { Length: > 0 } rowVersion)
            repository.SetOriginalRowVersion(entity, rowVersion);

        var result = await MutateAsync(entity, command, context, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
            return Result.Failure<TEntity>(result.Errors);

        // The idempotent no-op: the command is already satisfied, so there is nothing to save and
        // nothing to report as written. Neither post-save hook runs.
        if (context.SaveSkipped)
            return Result.Success(entity);

        await attemptUnitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogMutated(entity, command, context);
        await OnMutatedAsync(entity, command, context, cancellationToken).ConfigureAwait(false);

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

/// <summary>
/// A write handler over an existing aggregate that answers with a payload of its own choosing: the
/// update-style commands whose response is not the aggregate's DTO but a purpose-built envelope
/// (the refreshed DTO plus a warning the caller has to surface, a projection of one changed field,
/// a receipt for the file the write replaced).
/// </summary>
/// <remarks>
/// <para>
/// The third flavor exists because <typeparamref name="TResultPayload"/> is unconstrained: the DTO
/// flavor can only answer with an <see cref="IBaseDTO{TIdentifierType}"/> mapped by the aggregate's
/// registered mapper, which is exactly right for "the caller re-renders the aggregate" and wrong for
/// everything else. Here the handler builds the answer itself in
/// <see cref="BuildResult(TEntity, TCommand, MutationContext)"/>, reading both the mutated aggregate
/// and whatever the mutation wrote into the <see cref="MutationContext"/> while the aggregate was
/// loaded, so a pre-mutation value can reach the response without handler instance state.
/// </para>
/// <para>
/// It is a sibling of the other two rather than a fourth type parameter on the DTO flavor: generic
/// types overload by arity alone, so a four-parameter <c>MutateEntityHandlerBase</c> already exists.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TEntity">The aggregate root being mutated.</typeparam>
/// <typeparam name="TIdentifierType">The aggregate's primary key type.</typeparam>
/// <typeparam name="TResultPayload">The payload returned on success.</typeparam>
public abstract class MutateEntityPayloadHandlerBase<TCommand, TEntity, TIdentifierType, TResultPayload>(
    IUnitOfWork unitOfWork)
    : MutateEntityHandlerCore<TCommand, TEntity, TIdentifierType>(unitOfWork),
      ICommandHandler<TCommand, Result<TResultPayload>>
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public virtual async Task<Result<TResultPayload>> HandleAsync(
        TCommand command,
        CancellationToken cancellationToken = default)
    {
        var context = CreateContext();

        var result = await MutateCoreAsync(UnitOfWork, command, context, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? Result.Failure<TResultPayload>(result.Errors)
            : BuildResult(result.Value!, command, context);
    }

    /// <summary>
    /// Builds the handler's answer from the mutated aggregate, the command, and the side data the
    /// mutation wrote. Called only on success, after the save (or after a
    /// <see cref="MutationContext.SkipSave"/> short-circuit, where the aggregate is the one that was
    /// loaded and left untouched).
    /// </summary>
    /// <param name="entity">The mutated aggregate.</param>
    /// <param name="command">The command that was handled.</param>
    /// <param name="context">This run's mutation context, carrying whatever the mutation wrote.</param>
    /// <returns>The payload to answer with, or a failure when the payload cannot be built.</returns>
    protected abstract Result<TResultPayload> BuildResult(TEntity entity, TCommand command, MutationContext context);
}
