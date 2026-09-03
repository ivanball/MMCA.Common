using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.UI.Common.Interfaces;

/// <summary>
/// Generic CRUD service contract for UI modules. Each module provides an implementation
/// (via <see cref="MMCA.Common.UI.Services.Api.EntityServiceBase{TEntityDTO, TIdentifierType}"/>)
/// that calls the corresponding WebAPI endpoints over HTTP.
/// <para>
/// Every member returns a <see cref="Result"/>: the same railway type the server produced, read
/// back from its Problem Details response with the original <see cref="ErrorType"/> intact. A page
/// therefore branches on the outcome instead of catching an exception, and
/// <c>MMCA.Common.UI.Common.ResultUiExtensions</c> carries the four things it then does with a
/// failure.
/// </para>
/// </summary>
/// <typeparam name="TEntityDTO">DTO type returned by the API.</typeparam>
/// <typeparam name="TIdentifierType">Primary key type of the entity.</typeparam>
public interface IEntityService<TEntityDTO, TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Retrieves all entities, optionally including FK data and child collections.</summary>
    Task<Result<IReadOnlyList<TEntityDTO>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        CancellationToken cancellationToken = default);

    /// <summary>Server-side paged query with dynamic filters, sorting, and optional child inclusion.</summary>
    Task<Result<(IReadOnlyList<TEntityDTO> Items, int TotalItems)>> GetPagedAsync(
        Dictionary<string, (string Operator, string Value)> filters,
        int pageNumber,
        int pageSize,
        string? sortColumn,
        string? sortDirection,
        bool includeChildren = false,
        CancellationToken cancellationToken = default);

    /// <summary>Lightweight lookup list (Id + Name) used for dropdowns and autocomplete fields.</summary>
    Task<Result<IReadOnlyList<BaseLookup<TIdentifierType>>>> GetAllForLookupAsync(
        string nameProperty,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single entity by its primary key. A missing entity is an
    /// <see cref="ErrorType.NotFound"/> failure (see
    /// <c>ResultUiExtensions.IsNotFound</c>), never a success carrying null.
    /// </summary>
    Task<Result<TEntityDTO>> GetByIdAsync(
        TIdentifierType id,
        bool includeChildren = false,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a new entity and returns the server-assigned DTO (including generated Id).</summary>
    Task<Result<TEntityDTO>> AddAsync(
        TEntityDTO entity,
        CancellationToken cancellationToken = default);

    /// <summary>Updates an existing entity.</summary>
    Task<Result> UpdateAsync(
        TEntityDTO entity,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an entity by Id.</summary>
    Task<Result> DeleteAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default);
}
