using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.Services;

/// <summary>
/// Abstract base class for entity-to-DTO mappers. Provides a default <see cref="MapToDTOs"/>
/// implementation that delegates to <see cref="MapToDTO"/> for each entity, eliminating
/// boilerplate in concrete mappers.
/// </summary>
/// <typeparam name="TEntity">The domain entity type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public abstract class EntityDTOMapperBase<TEntity, TEntityDTO, TIdentifierType>
    : IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public abstract TEntityDTO MapToDTO(TEntity entity);

    /// <inheritdoc />
    public IReadOnlyCollection<TEntityDTO> MapToDTOs(IReadOnlyCollection<TEntity> entityCollection)
    {
        ArgumentNullException.ThrowIfNull(entityCollection);

        return [.. entityCollection.Select(MapToDTO)];
    }
}
