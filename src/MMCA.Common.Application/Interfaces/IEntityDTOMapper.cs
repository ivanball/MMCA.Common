using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.Interfaces;

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
    IReadOnlyCollection<TEntityDTO> MapToDTOs(IReadOnlyCollection<TEntity> entityCollection);
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
