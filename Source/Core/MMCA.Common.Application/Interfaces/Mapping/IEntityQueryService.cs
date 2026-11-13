using System.Linq.Expressions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.Interfaces.Mapping;

/// <summary>
/// Provides generic query operations (GetAll, GetById, Exists) with support for
/// navigation includes, dynamic filtering, sorting, pagination, and field projection.
/// When no field subset is requested the typed DTOs are returned as-is (no per-row
/// shaping cost); an explicit <c>fields</c> selection returns dynamically shaped objects.
/// Both serialize to the same camelCase JSON.
/// </summary>
/// <typeparam name="TEntity">The domain entity type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO type for entity mapping.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IEntityQueryService<TEntity, TEntityDTO, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>
    /// Error code of the failure the default <see cref="GetPageByCursorAsync"/> returns: the
    /// implementation predates the keyset read and cannot serve it.
    /// </summary>
    const string CursorPagingNotSupportedErrorCode = "Query.CursorPagingNotSupported";

    /// <summary>
    /// Error code of the failure the default <see cref="GetChangesSinceAsync"/> returns: the
    /// implementation predates the change feed and cannot serve it.
    /// </summary>
    const string ChangeFeedNotSupportedErrorCode = "Query.ChangeFeedNotSupported";

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
    /// <returns>A paged result of DTOs (typed, or shaped when a field subset was requested).</returns>
    Task<Result<PagedCollectionResult<object>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        ISpecification<TEntity, TIdentifierType>? specification = null,
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
    /// <returns>A paged result of DTOs (typed, or shaped when a field subset was requested) with pagination metadata.</returns>
    Task<Result<PagedCollectionResult<object>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        ISpecification<TEntity, TIdentifierType>? specification = null,
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
    /// <param name="asTracking">Whether to track entities in the EF change tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of id/name lookup pairs, ordered by display name.</returns>
    /// <remarks>
    /// Results are always ordered by the projected display name (the repository orders after the
    /// id/name projection). An <c>orderBy</c> parameter existed here through v1.138.0 but was never
    /// honored (the repository contract has no ordering hook), so it was removed rather than widened;
    /// see the extraction plan if entity-level lookup ordering ever becomes a real requirement.
    /// </remarks>
    Task<Result<IReadOnlyCollection<BaseLookup<TIdentifierType>>>> GetAllForLookupAsync(
        string nameProperty,
        Expression<Func<TEntity, bool>>? where = null,
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
        ISpecification<TEntity, TIdentifierType>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single entity by its identifier, returning its DTO
    /// (typed, or shaped when a field subset was requested).
    /// </summary>
    /// <param name="id">The typed identifier value.</param>
    /// <param name="includeFKs">Whether to include FK reference navigations.</param>
    /// <param name="includeChildren">Whether to include child collection navigations.</param>
    /// <param name="specification">Optional specification for authorization filtering.</param>
    /// <param name="fields">Comma-separated list of fields to include in the response.</param>
    /// <param name="asTracking">Whether to track the entity in the EF change tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO or a NotFound error.</returns>
    Task<Result<object>> GetByIdAsync(
        TIdentifierType id,
        bool includeFKs = false,
        bool includeChildren = false,
        ISpecification<TEntity, TIdentifierType>? specification = null,
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

    /// <summary>
    /// Returns one keyset ("seek") page of DTOs: the rows after <see cref="KeysetPageRequest.Cursor"/>,
    /// ordered by the single <see cref="KeysetPageRequest.SortColumn"/> (a DTO name, validated against
    /// the response contract) with <c>Id</c> as tie-break, plus the cursor of the next page.
    /// </summary>
    /// <param name="request">The page size, sort column, direction and cursor.</param>
    /// <param name="specification">Optional specification whose <c>Criteria</c> scopes the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The page, or a failure: a validation failure for an unknown sort column or a malformed cursor;
    /// <see cref="CursorPagingNotSupportedErrorCode"/> (unexpected, 500) from this default body, which
    /// an implementation that predates the member inherits.
    /// </returns>
    /// <remarks>No includes, field projection or filter dictionary apply: the keyset read takes a specification only.</remarks>
    Task<Result<KeysetCollectionResult<TEntityDTO>>> GetPageByCursorAsync(
        KeysetPageRequest request,
        ISpecification<TEntity, TIdentifierType>? specification = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<KeysetCollectionResult<TEntityDTO>>(Error.Unexpected(
            CursorPagingNotSupportedErrorCode,
            $"The query service for {typeof(TEntity).Name} does not support cursor paging.",
            nameof(GetPageByCursorAsync),
            typeof(TEntity).Name)));

    /// <summary>
    /// Returns one page of the change feed: every row whose <c>LastModifiedOn</c> is at or after
    /// <paramref name="since"/> (soft-deleted rows included), ordered by <c>(LastModifiedOn, Id)</c>
    /// ascending, each as an upsert carrying the mapped DTO or as a tombstone carrying no item.
    /// </summary>
    /// <param name="since">
    /// The inclusive lower bound, or <see langword="null"/> for no time bound. A local value is converted
    /// to UTC and an unspecified one is taken as UTC.
    /// </param>
    /// <param name="cursor">The opaque cursor of the previous page, or <see langword="null"/> for the first page.</param>
    /// <param name="pageSize">The number of changes to return (clamped by the keyset read).</param>
    /// <param name="specification">Optional scope, ANDed with the time bound.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The page of changes, or a failure: a validation failure for a malformed cursor;
    /// <see cref="ChangeFeedNotSupportedErrorCode"/> (unexpected, 500) from this default body.
    /// </returns>
    /// <remarks>
    /// The client pages with the same <paramref name="since"/> until the next cursor is null, then keeps
    /// the largest <c>ChangedOn</c> as its next <paramref name="since"/>. Because the bound is inclusive,
    /// boundary rows come again and are applied idempotently. A row that left the scope without being
    /// deleted yields no tombstone.
    /// </remarks>
    Task<Result<KeysetCollectionResult<EntityChange<TEntityDTO, TIdentifierType>>>> GetChangesSinceAsync(
        DateTime? since,
        string? cursor,
        int pageSize,
        ISpecification<TEntity, TIdentifierType>? specification = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<KeysetCollectionResult<EntityChange<TEntityDTO, TIdentifierType>>>(Error.Unexpected(
            ChangeFeedNotSupportedErrorCode,
            $"The query service for {typeof(TEntity).Name} does not support the change feed.",
            nameof(GetChangesSinceAsync),
            typeof(TEntity).Name)));
}
