using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.UseCases;

/// <summary>
/// The shared create-an-aggregate workflow: map the request through the entity's factory, add the
/// new aggregate to its repository, save, and return the mapped DTO. A module's create handler
/// subclasses this and adds only what is genuinely its own (its <c>[LoggerMessage]</c> partial, any
/// pre-map validation, any post-commit publish).
/// </summary>
/// <remarks>
/// <para>
/// The base is deliberately an <b>abstract class that implements
/// <see cref="ICommandHandler{TCommand, TResult}"/></b>, exactly like the shipped
/// <c>ForgotPasswordHandlerBase</c>: the concrete app subclass stays the registered handler, so
/// <c>ScanModuleApplicationServices</c> keeps discovering it and the decorator pipeline keeps
/// wrapping it. Scrutor never registers the abstract base itself.
/// </para>
/// <para>
/// The repository is obtained from <see cref="IUnitOfWork"/> inside the workflow rather than
/// constructor-injected: <c>IRepository&lt;,&gt;</c> must never be injected directly, because only the
/// unit of work knows which physical data source the entity resolves to.
/// </para>
/// <para>
/// <b>Manual-id retry variants</b> (create paths that compute the primary key themselves and retry on
/// a unique-constraint collision) override <see cref="HandleAsync"/> to wrap
/// <see cref="CreateCoreAsync"/> in their retry loop, and override
/// <see cref="PrepareAsync"/> to recompute the id per attempt. Because <c>CreateCoreAsync</c> takes
/// the unit of work as a parameter, a retry can run against a fresh DI scope's unit of work while
/// still reusing the whole workflow.
/// </para>
/// </remarks>
/// <typeparam name="TCreateRequest">The create request DTO, which is also the command.</typeparam>
/// <typeparam name="TEntity">The aggregate root being created.</typeparam>
/// <typeparam name="TIdentifierType">The aggregate's primary key type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO returned on success.</typeparam>
public abstract class CreateEntityHandlerBase<TCreateRequest, TEntity, TIdentifierType, TEntityDTO>(
    IUnitOfWork unitOfWork,
    IEntityRequestMapper<TEntity, TCreateRequest, TIdentifierType> requestMapper,
    IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType> dtoMapper)
    : ICommandHandler<TCreateRequest, Result<TEntityDTO>>
    where TCreateRequest : ICreateRequest
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
    where TEntityDTO : IBaseDTO<TIdentifierType>
{
    /// <summary>The ambient unit of work (exposed so an override can reach a read repository).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <inheritdoc />
    public virtual async Task<Result<TEntityDTO>> HandleAsync(
        TCreateRequest command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return await CreateCoreAsync(unitOfWork, command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one create attempt against <paramref name="attemptUnitOfWork"/>: prepare the request,
    /// map it through the entity factory, add, save, log, and map the result to its DTO.
    /// </summary>
    /// <param name="attemptUnitOfWork">
    /// The unit of work this attempt runs against. Normally the injected one; a retrying subclass
    /// passes a fresh scope's unit of work, because the ambient DbContext still tracks the failed
    /// insert and a clean context is required for a recomputed id to persist.
    /// </param>
    /// <param name="command">The create request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created entity's DTO, or the failure that stopped the create.</returns>
    protected async Task<Result<TEntityDTO>> CreateCoreAsync(
        IUnitOfWork attemptUnitOfWork,
        TCreateRequest command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attemptUnitOfWork);

        var prepared = await PrepareAsync(attemptUnitOfWork, command, cancellationToken).ConfigureAwait(false);
        if (prepared.IsFailure)
            return Result.Failure<TEntityDTO>(prepared.Errors);

        var request = prepared.Value!;

        var result = await requestMapper.CreateEntityAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
            return Result.Failure<TEntityDTO>(result.Errors);

        var entity = result.Value!;
        var repository = attemptUnitOfWork.GetRepository<TEntity, TIdentifierType>();

        await PersistAsync(attemptUnitOfWork, repository, entity, cancellationToken).ConfigureAwait(false);

        LogCreated(entity);
        await OnCreatedAsync(entity, cancellationToken).ConfigureAwait(false);

        return Result.Success(dtoMapper.MapToDTO(entity));
    }

    /// <summary>
    /// Optional pre-map step, run before the request reaches the entity factory. The default is a
    /// pass-through. Override to resolve an app-assigned primary key, or to run a cross-aggregate
    /// validation whose failure must stop the create.
    /// </summary>
    /// <param name="attemptUnitOfWork">The unit of work this attempt runs against.</param>
    /// <param name="command">The incoming create request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The request to map (possibly rewritten), or a failure that stops the create.</returns>
    protected virtual Task<Result<TCreateRequest>> PrepareAsync(
        IUnitOfWork attemptUnitOfWork,
        TCreateRequest command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(command));

    /// <summary>
    /// Persists the new aggregate. The default adds it to the repository and saves, which is what
    /// every create path does today. Override only when a create needs a different persist step.
    /// </summary>
    /// <param name="attemptUnitOfWork">The unit of work this attempt runs against.</param>
    /// <param name="repository">The aggregate's repository, already resolved from the unit of work.</param>
    /// <param name="entity">The entity produced by the request mapper.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected virtual async Task PersistAsync(
        IUnitOfWork attemptUnitOfWork,
        IRepository<TEntity, TIdentifierType> repository,
        TEntity entity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attemptUnitOfWork);
        ArgumentNullException.ThrowIfNull(repository);

        await repository.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        await attemptUnitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Structured-logging hook, called after a successful save. The default does nothing; override it
    /// with a call to the module's own <c>[LoggerMessage]</c> partial, which is where the message
    /// template and its entity-specific fields belong.
    /// </summary>
    /// <param name="entity">The persisted entity, with its database-generated id populated.</param>
    protected virtual void LogCreated(TEntity entity)
    {
        // No-op by design: logging is per-module vocabulary, so the base only provides the call site.
    }

    /// <summary>
    /// Post-commit hook, called after <see cref="LogCreated"/> and before the DTO is mapped. The
    /// default does nothing. Override it to publish an integration event carrying the now-known
    /// database-generated id.
    /// </summary>
    /// <param name="entity">The persisted entity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected virtual Task OnCreatedAsync(TEntity entity, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
