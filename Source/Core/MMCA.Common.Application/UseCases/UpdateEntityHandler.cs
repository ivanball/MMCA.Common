using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.UseCases;

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
    protected override byte[]? RowVersion(UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType> command)
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
