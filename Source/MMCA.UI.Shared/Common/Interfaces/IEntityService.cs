using MMCA.Common.Shared.DTOs;

namespace MMCA.UI.Shared.Common.Interfaces;

/// <summary>
/// Generic CRUD service contract for UI modules. Each module provides an implementation
/// (via <see cref="MMCA.UI.Shared.Services.EntityServiceBase{TEntityDTO, TIdentifierType}"/>)
/// that calls the corresponding WebAPI endpoints over HTTP.
/// </summary>
/// <typeparam name="TEntityDTO">DTO type returned by the API.</typeparam>
/// <typeparam name="TIdentifierType">Primary key type of the entity.</typeparam>
public interface IEntityService<TEntityDTO, TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Retrieves all entities, optionally including FK data and child collections.</summary>
    Task<IReadOnlyList<TEntityDTO>?> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        CancellationToken cancellationToken = default);

    /// <summary>Server-side paged query with dynamic filters, sorting, and optional child inclusion.</summary>
    Task<(IReadOnlyList<TEntityDTO> Items, int TotalItems)> GetPagedAsync(
        Dictionary<string, (string Operator, string Value)> filters,
        int pageNumber,
        int pageSize,
        string? sortColumn,
        string? sortDirection,
        bool includeChildren = false,
        CancellationToken cancellationToken = default);

    /// <summary>Lightweight lookup list (Id + Name) used for dropdowns and autocomplete fields.</summary>
    Task<IReadOnlyList<BaseLookup<TIdentifierType>>> GetAllForLookupAsync(
        string nameProperty,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a single entity by its primary key. Returns <see langword="null"/> on 404.</summary>
    Task<TEntityDTO?> GetByIdAsync(
        TIdentifierType id,
        bool includeChildren = false,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a new entity and returns the server-assigned DTO (including generated Id).</summary>
    Task<TEntityDTO> AddAsync(
        TEntityDTO entity,
        CancellationToken cancellationToken = default);

    /// <summary>Updates an existing entity. Returns <see langword="true"/> on success.</summary>
    Task<bool> UpdateAsync(
        TEntityDTO entity,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an entity by Id. Returns <see langword="true"/> on success.</summary>
    Task<bool> DeleteAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default);
}
