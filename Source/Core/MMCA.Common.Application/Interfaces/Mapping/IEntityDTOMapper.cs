using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.Interfaces.Mapping;

/// <summary>
/// Maps domain entities to their corresponding DTOs. Implementations are auto-registered
/// via Scrutor assembly scanning.
/// </summary>
/// <typeparam name="TEntity">The domain entity type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Maps a single entity to its DTO representation.</summary>
    /// <param name="entity">The entity to map.</param>
    /// <returns>The mapped DTO.</returns>
    TEntityDTO MapToDTO(TEntity entity);

    /// <summary>Maps a collection of entities to their DTO representations.</summary>
    /// <param name="entityCollection">The entities to map.</param>
    /// <returns>A read-only collection of mapped DTOs.</returns>
    IReadOnlyCollection<TEntityDTO> MapToDTOs(IReadOnlyCollection<TEntity> entityCollection)
    {
        ArgumentNullException.ThrowIfNull(entityCollection);

        return [.. entityCollection.Select(MapToDTO)];
    }
}

/// <summary>
/// Maps incoming create requests to domain entities via the entity's factory method.
/// Encapsulates the request-to-entity mapping and any async validation (e.g. uniqueness checks).
/// </summary>
/// <typeparam name="TEntity">The domain entity type to create.</typeparam>
/// <typeparam name="TCreateRequest">The incoming request DTO.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IEntityRequestMapper<TEntity, TCreateRequest, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TCreateRequest : ICreateRequest
    where TIdentifierType : notnull
{
    /// <summary>
    /// Creates a domain entity from the request, returning a <see cref="Result{T}"/>
    /// that may contain validation errors.
    /// </summary>
    /// <param name="request">The create request to map.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the created entity or validation errors.</returns>
    Task<Result<TEntity>> CreateEntityAsync(TCreateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies an incoming update request to an already-loaded aggregate by calling that aggregate's own
/// guarded mutation methods. Implementations are auto-registered via Scrutor assembly scanning,
/// exactly like <see cref="IEntityRequestMapper{TEntity, TCreateRequest, TIdentifierType}"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the write-side twin of the create mapper: the mapper owns "request to a new aggregate",
/// the applier owns "request onto an existing aggregate". Putting the mutation behind this contract
/// is what lets the framework ship one generic update handler
/// (<c>UpdateEntityHandler</c>): the handler owns loading, the optimistic-concurrency token and the
/// save, while the aggregate keeps owning its invariants and the domain events it raises.
/// </para>
/// <para>
/// The applier answers with a bare <see cref="Result"/> rather than a new entity, because the
/// instance handed in is the tracked one: a successful apply has already mutated it in place, and a
/// failure must leave it untouched so nothing reaches the database.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The domain entity type being updated.</typeparam>
/// <typeparam name="TUpdateRequest">The incoming update request DTO.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IEntityUpdateApplier<TEntity, TUpdateRequest, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>
    /// Applies the request to the loaded aggregate, returning the aggregate's own
    /// <see cref="Result"/> so a refused invariant stops the write before it is saved.
    /// </summary>
    /// <param name="entity">The loaded, tracked aggregate to mutate.</param>
    /// <param name="request">The update request to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success when the aggregate accepted every change, otherwise the refusal.</returns>
    Task<Result> ApplyAsync(TEntity entity, TUpdateRequest request, CancellationToken cancellationToken = default);
}
