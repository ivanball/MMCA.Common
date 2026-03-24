using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.ModelBinders;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// Contract for read-only entity controllers exposing GET all, paged, lookup, and by-id endpoints.
/// </summary>
/// <typeparam name="TEntityDTO">The DTO type returned to clients.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IEntityControllerBase<
    TEntityDTO,
    TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Returns all entities with optional field projection and eager loading.</summary>
    /// <param name="fields">Comma-separated DTO property names for field projection.</param>
    /// <param name="includeFKs">When true, eagerly loads foreign key navigation properties.</param>
    /// <param name="includeChildren">When true, eagerly loads child collections.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of entity DTOs.</returns>
    Task<ActionResult<CollectionResult<TEntityDTO>>> GetAllAsync(
        [FromQuery] string? fields = null,
        bool includeFKs = false,
        bool includeChildren = false,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a paged, filterable, sortable collection of entities.</summary>
    /// <param name="includeFKs">When true, eagerly loads foreign key navigation properties.</param>
    /// <param name="includeChildren">When true, eagerly loads child collections.</param>
    /// <param name="sortColumn">DTO property name to sort by.</param>
    /// <param name="sortDirection">Sort direction: "asc" or "desc".</param>
    /// <param name="fields">Comma-separated DTO property names for field projection.</param>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="filters">Query string filters parsed by <see cref="QueryFilterModelBinder"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paged collection of entity DTOs with pagination metadata.</returns>
    Task<ActionResult<PagedCollectionResult<TEntityDTO>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        string? sortColumn = null,
        string? sortDirection = null,
        [FromQuery] string? fields = null,
        [Range(1, int.MaxValue)] int pageNumber = 1,
        [Range(1, int.MaxValue)] int pageSize = 10,
        [ModelBinder(typeof(QueryFilterModelBinder))] Dictionary<string, (string Operator, string Value)>? filters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a lightweight id/name collection for dropdown population.</summary>
    /// <param name="nameProperty">DTO property name to use as the display label.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of lookup entries.</returns>
    Task<ActionResult<CollectionResult<BaseLookup<TIdentifierType>>>> GetAllForLookupAsync(
        string nameProperty,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a single entity by its identifier.</summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="includeFKs">When true, eagerly loads foreign key navigation properties.</param>
    /// <param name="includeChildren">When true, eagerly loads child collections.</param>
    /// <param name="fields">Comma-separated DTO property names for field projection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entity DTO if found; otherwise a 404 Problem Details response.</returns>
    Task<ActionResult<TEntityDTO>> GetByIdAsync(
        TIdentifierType id,
        bool includeFKs = true,
        bool includeChildren = false,
        [FromQuery] string? fields = null,
        CancellationToken cancellationToken = default);
}
