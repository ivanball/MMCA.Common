using System.Dynamic;
using System.Linq.Expressions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Provides generic query operations (GetAll, GetById, Exists) with support for
/// navigation includes, dynamic filtering, sorting, pagination, and field projection.
/// Returns DTOs shaped as <see cref="ExpandoObject"/> for flexible field selection.
/// </summary>
/// <typeparam name="TEntity">The domain entity type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO type for entity mapping.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IEntityQueryService<TEntity, TEntityDTO, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Gets the DTO mapper for manual entity-to-DTO conversion outside the query pipeline.</summary>
    IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType> DTOMapper { get; }

    /// <summary>
    /// Retrieves all entities with optional navigation includes, specification criteria, and field projection.
    /// </summary>
    /// <param name="includeFKs">Whether to include FK reference navigations.</param>
    /// <param name="includeChildren">Whether to include child collection navigations.</param>
    /// <param name="specification">Optional specification for authorization/business rule filtering.</param>
    /// <param name="fields">Comma-separated list of fields to include in the response.</param>
    /// <param name="asTracking">Whether to track entities in the EF change tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paged result of shaped DTOs.</returns>
    Task<Result<PagedCollectionResult<ExpandoObject>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        Specification<TEntity, TIdentifierType>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves entities with full query capabilities: filtering, sorting, pagination, and field projection.
    /// </summary>
    /// <param name="includeFKs">Whether to include FK reference navigations.</param>
    /// <param name="includeChildren">Whether to include child collection navigations.</param>
    /// <param name="specification">Optional specification for authorization/business rule filtering.</param>
    /// <param name="filters">Dynamic filters as property name to (operator, value) pairs.</param>
    /// <param name="sortColumn">The property name to sort by.</param>
    /// <param name="sortDirection">"asc" or "desc".</param>
    /// <param name="fields">Comma-separated list of fields to include in the response.</param>
    /// <param name="pageNumber">1-based page number for pagination.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="asTracking">Whether to track entities in the EF change tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paged result of shaped DTOs with pagination metadata.</returns>
    Task<Result<PagedCollectionResult<ExpandoObject>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        Specification<TEntity, TIdentifierType>? specification = null,
        Dictionary<string, (string Operator, string Value)>? filters = null,
        string? sortColumn = null,
        string? sortDirection = null,
        string? fields = null,
        int? pageNumber = null,
        int? pageSize = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves entities as lightweight id/name pairs for dropdown/lookup scenarios.
    /// </summary>
    /// <param name="nameProperty">The entity property to use as the display name.</param>
    /// <param name="where">Optional filter expression.</param>
    /// <param name="orderBy">Optional ordering expression.</param>
    /// <param name="asTracking">Whether to track entities in the EF change tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of id/name lookup pairs.</returns>
    Task<Result<IReadOnlyCollection<BaseLookup<TIdentifierType>>>> GetAllForLookupAsync(
        string nameProperty,
        Expression<Func<TEntity, bool>>? where = null,
        Expression<Func<TEntity, string>>? orderBy = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single entity by its identifier, returning the full entity for use in command handlers.
    /// </summary>
    /// <param name="idValue">The identifier value as a string.</param>
    /// <param name="idField">The property name to filter by (defaults to "Id").</param>
    /// <param name="includeFKs">Whether to include FK reference navigations.</param>
    /// <param name="includeChildren">Whether to include child collection navigations.</param>
    /// <param name="specification">Optional specification for authorization filtering.</param>
    /// <param name="fields">Comma-separated list of fields to include.</param>
    /// <param name="asTracking">Whether to track the entity in the EF change tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entity or a NotFound error.</returns>
    Task<Result<TEntity>> GetEntityByIdAsync(
        string idValue,
        string? idField = null,
        bool includeFKs = false,
        bool includeChildren = false,
        Specification<TEntity, TIdentifierType>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single entity by its identifier, returning a shaped DTO as an <see cref="ExpandoObject"/>.
    /// </summary>
    /// <param name="id">The typed identifier value.</param>
    /// <param name="includeFKs">Whether to include FK reference navigations.</param>
    /// <param name="includeChildren">Whether to include child collection navigations.</param>
    /// <param name="specification">Optional specification for authorization filtering.</param>
    /// <param name="fields">Comma-separated list of fields to include in the response.</param>
    /// <param name="asTracking">Whether to track the entity in the EF change tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A shaped DTO or a NotFound error.</returns>
    Task<Result<ExpandoObject>> GetByIdAsync(
        TIdentifierType id,
        bool includeFKs = false,
        bool includeChildren = false,
        Specification<TEntity, TIdentifierType>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether any entity matches the given predicate.
    /// </summary>
    /// <param name="where">The filter predicate.</param>
    /// <param name="ignoreQueryFilters">Whether to bypass global query filters (e.g. soft-delete).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if a matching entity exists.</returns>
    Task<bool> ExistsAsync(
        Expression<Func<TEntity, bool>> where,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);
}
