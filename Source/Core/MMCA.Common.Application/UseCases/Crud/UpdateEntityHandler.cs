using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.UseCases.Crud;

/// <summary>
/// Generic update handler that works for any aggregate root entity: loads the aggregate, stamps the
/// caller's optimistic-concurrency token, hands the request to the module's
/// <see cref="IEntityUpdateApplier{TEntity, TUpdateRequest, TIdentifierType}"/>, saves only when the
/// aggregate accepted the change, and answers with the refreshed DTO.
/// </summary>
/// <remarks>
/// <para>
/// It is the update counterpart of <see cref="DeleteEntityHandler{TEntity, TIdentifierType}"/>, and
/// like that handler it is left unsealed so a module can subclass it to declare the
/// <c>Includes</c> a particular aggregate's mutation needs, or to add a
/// <c>[LoggerMessage]</c> partial, without giving up the shared workflow.
/// </para>
/// <para>
/// <b>Post-load, pre-mutate work needs no new hook.</b> A subclass that has to touch the aggregate
/// after it is loaded and before the applier runs (the common case being ADR-035's second
/// concurrency token: <c>SetOriginalRowVersion</c> on a tracked <b>child</b> row that a nested update
/// addresses, which the base's <c>RowVersion</c> hook cannot reach because that hook stamps the root)
/// overrides <c>MutateAsync</c>, does its work against
/// <c>UnitOfWork.GetRepository&lt;TEntity, TIdentifierType&gt;()</c>, and then awaits
/// <c>base.MutateAsync(...)</c> to run the applier. The aggregate is already loaded and already
/// root-stamped at that point, and a refusal returned before the <see langword="base"/> call stops
/// the write.
/// </para>
/// <para>
/// <b>No events are raised here.</b> Domain events belong to the aggregate's own mutation methods,
/// which the applier calls; a handler that published anything of its own would fire for the generic
/// path and stay silent for a hand-written one.
/// </para>
/// <para>
/// The repository comes from <see cref="IUnitOfWork"/> via the inherited workflow and is never
/// constructor-injected: only the unit of work knows which physical data source the aggregate
/// resolves to.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The aggregate root entity type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO returned on success.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <typeparam name="TUpdateRequest">The update request the applier consumes.</typeparam>
public class UpdateEntityHandler<TEntity, TEntityDTO, TIdentifierType, TUpdateRequest>(
    IUnitOfWork unitOfWork,
    IEntityUpdateApplier<TEntity, TUpdateRequest, TIdentifierType> updateApplier,
    IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType> dtoMapper)
    : MutateEntityHandlerBase<
        UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>,
        TEntity,
        TIdentifierType,
        TEntityDTO>(unitOfWork, dtoMapper)
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
    where TEntityDTO : IBaseDTO<TIdentifierType>
{
    /// <summary>
    /// Reports the open handler name rather than the runtime <c>`4</c>-suffixed one, so a
    /// <c>NotFound</c> failure reads the same as the hand-written handlers it replaces.
    /// </summary>
    protected override string HandlerName => nameof(UpdateEntityHandler<,,,>);

    /// <inheritdoc />
    protected override TIdentifierType EntityId(UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Id;
    }

    /// <inheritdoc />
    protected override byte[] RowVersion(UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.RowVersion;
    }

    /// <inheritdoc />
    protected override Task<Result> MutateAsync(
        TEntity entity,
        UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType> command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return updateApplier.ApplyAsync(entity, command.Request, cancellationToken);
    }
}

/// <summary>
/// The verb-discriminated generic update handler: identical to
/// <see cref="UpdateEntityHandler{TEntity, TEntityDTO, TIdentifierType, TUpdateRequest}"/> except
/// that it serves
/// <see cref="UpdateEntityCommand{TEntity, TUpdateRequest, TIdentifierType, TApplier}"/> and resolves
/// the applier by its concrete type, so an aggregate can have several verbs over one request DTO.
/// </summary>
/// <remarks>
/// <para>
/// Register one per verb with <c>AddEntityUpdateVerb</c>. The applier itself is an ordinary
/// <see cref="IEntityUpdateApplier{TEntity, TUpdateRequest, TIdentifierType}"/> picked up by the
/// module scan (which registers it as itself as well as by its interfaces, which is what makes the
/// concrete-typed injection here resolvable); only the command and the handler are verb-specific.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The aggregate root entity type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO returned on success.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <typeparam name="TUpdateRequest">The update request the applier consumes.</typeparam>
/// <typeparam name="TApplier">The applier serving this verb.</typeparam>
public class UpdateEntityHandler<TEntity, TEntityDTO, TIdentifierType, TUpdateRequest, TApplier>(
    IUnitOfWork unitOfWork,
    TApplier updateApplier,
    IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType> dtoMapper)
    : MutateEntityHandlerBase<
        UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType, TApplier>,
        TEntity,
        TIdentifierType,
        TEntityDTO>(unitOfWork, dtoMapper)
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TApplier : class, IEntityUpdateApplier<TEntity, TUpdateRequest, TIdentifierType>
{
    /// <summary>
    /// Reports the open handler name plus the verb's applier, so two verbs over one aggregate produce
    /// distinguishable <c>NotFound</c> failures.
    /// </summary>
    protected override string HandlerName => $"{nameof(UpdateEntityHandler<,,,,>)}<{typeof(TApplier).Name}>";

    /// <inheritdoc />
    protected override TIdentifierType EntityId(
        UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType, TApplier> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Id;
    }

    /// <inheritdoc />
    protected override byte[] RowVersion(
        UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType, TApplier> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.RowVersion;
    }

    /// <inheritdoc />
    protected override Task<Result> MutateAsync(
        TEntity entity,
        UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType, TApplier> command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return updateApplier.ApplyAsync(entity, command.Request, cancellationToken);
    }
}

/// <summary>
/// The generic update handler for a <b>derived</b> command: an
/// <see cref="UpdateEntityCommand{TEntity, TUpdateRequest, TIdentifierType}"/> subclass that carries
/// state beside the request (a route-derived child id, a server-decided flag, a second concurrency
/// token). It runs the same load-stamp-apply-save workflow but hands the whole command to an
/// <see cref="IEntityUpdateCommandApplier{TEntity, TUpdateRequest, TIdentifierType, TCommand}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Register it with <c>AddEntityUpdate</c>, which closes it over the derived command and bridges that
/// command to <c>IValidator&lt;TUpdateRequest&gt;</c>. Everything else is unchanged: the inherited
/// <c>RowVersion</c> still stamps the root under ADR-035, the inherited <c>CachePrefix</c> still
/// evicts the aggregate's cached reads, and events stay the aggregate's job.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The derived command type.</typeparam>
/// <typeparam name="TEntity">The aggregate root entity type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO returned on success.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <typeparam name="TUpdateRequest">The update request the command carries.</typeparam>
public class UpdateEntityCommandHandler<TCommand, TEntity, TEntityDTO, TIdentifierType, TUpdateRequest>(
    IUnitOfWork unitOfWork,
    IEntityUpdateCommandApplier<TEntity, TUpdateRequest, TIdentifierType, TCommand> updateApplier,
    IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType> dtoMapper)
    : MutateEntityHandlerBase<TCommand, TEntity, TIdentifierType, TEntityDTO>(unitOfWork, dtoMapper)
    where TCommand : UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
    where TEntityDTO : IBaseDTO<TIdentifierType>
{
    /// <summary>
    /// Reports the open handler name plus the command it serves, so several derived commands over one
    /// aggregate produce distinguishable <c>NotFound</c> failures.
    /// </summary>
    protected override string HandlerName => $"{nameof(UpdateEntityCommandHandler<,,,,>)}<{typeof(TCommand).Name}>";

    /// <inheritdoc />
    protected override TIdentifierType EntityId(TCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Id;
    }

    /// <inheritdoc />
    protected override byte[] RowVersion(TCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.RowVersion;
    }

    /// <inheritdoc />
    protected override Task<Result> MutateAsync(
        TEntity entity,
        TCommand command,
        MutationContext context,
        CancellationToken cancellationToken) =>
        updateApplier.ApplyAsync(entity, command, context, cancellationToken);
}
