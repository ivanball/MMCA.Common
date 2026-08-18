using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services.Filtering;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.Services.Query;

/// <summary>
/// Executes a multi-step query pipeline: include navigation properties, apply criteria
/// and filters, sort, paginate, and project fields. Uses a two-path strategy for
/// navigation includes depending on data source capabilities.
/// </summary>
public sealed class EntityQueryPipeline(IQueryableExecutor queryableExecutor) : IEntityQueryPipeline
{
    /// <summary>
    /// Framework safety ceiling (rubric §12): the maximum number of rows the pipeline will
    /// materialize. A query that omits pagination is capped at this ceiling; a paginated query
    /// has its page size clamped to this ceiling as well (defense in depth). The per-request page
    /// size is also clamped to <c>ApplicationSettings.MaxPageSize</c> at the API boundary, but this
    /// in-pipeline guard means a direct service caller that bypasses that boundary can never trigger
    /// an unbounded full-table load.
    /// </summary>
    public const int MaxUnboundedResultLimit = 1000;

    /// <summary>
    /// The key property appended as a final ascending sort key on every paginated read, making the
    /// order total. Every entity has it (<c>IBaseEntity.Id</c>), it is unique, and it is
    /// server-supplied rather than client input.
    /// </summary>
    /// <remarks>
    /// Only paginated reads get it. An unpaginated read materializes one capped set in one
    /// statement, so it cannot suffer the split-across-pages incoherence the tie-break exists to
    /// prevent, and adding an ORDER BY there would charge every unsorted list read for a sort the
    /// caller never asked for.
    /// </remarks>
    private const string PaginationTieBreakProperty = "Id";

    /// <inheritdoc />
    public async Task<(IReadOnlyCollection<TEntity> Items, int TotalCount)> ExecuteAsync<TEntity, TIdentifierType>(
        IQueryable<TEntity> baseQuery,
        NavigationMetadata navigationMetadata,
        EntityQueryParameters<TEntity> parameters,
        Func<IReadOnlyCollection<TEntity>, NavigationMetadata, bool, bool, CancellationToken, Task> navigationPopulator,
        CancellationToken cancellationToken)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        var query = ApplyIncludesCriteriaAndFilters<TEntity, TIdentifierType>(baseQuery, navigationMetadata, parameters);

        // PATH 2 - Unsupported includes: When the data source does not support JOINs
        // (e.g. Cosmos DB where entities may live in different containers), we must
        // materialize the query, then manually load related data via the NavigationPopulator.
        if (navigationMetadata.UnsupportedIncludes.Count != 0)
            return await ExecuteWithManualNavigationAsync<TEntity, TIdentifierType>(query, navigationMetadata, parameters, navigationPopulator, cancellationToken).ConfigureAwait(false);

        return await ExecuteWithServerSideIncludesAsync<TEntity, TIdentifierType>(query, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyCollection<TResult> Items, int TotalCount)> ExecuteProjectedAsync<TEntity, TResult, TIdentifierType>(
        IQueryable<TEntity> baseQuery,
        EntityQueryParameters<TEntity> parameters,
        Func<IQueryable<TEntity>, IQueryable<TResult>> project,
        CancellationToken cancellationToken)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(project);

        var query = baseQuery;

        if (parameters.Criteria is not null)
            query = query.Where(parameters.Criteria);

        if (parameters.Filters is not null && parameters.Filters.Count != 0)
            query = QueryFilterService.ApplyFilters(query, parameters.Filters, parameters.DTOToEntityPropertyMap);

        bool isPaginated = parameters.PageNumber.HasValue && parameters.PageSize.HasValue;

        query = QueryFieldService.ApplySorting(
            query,
            parameters.SortColumn,
            parameters.SortDirection,
            parameters.DTOToEntityPropertyMap,
            tieBreakProperty: isPaginated ? PaginationTieBreakProperty : null);

        int totalCount = 0;
        var unpagedQuery = query;

        if (isPaginated)
        {
            totalCount = await queryableExecutor.CountAsync(query, cancellationToken).ConfigureAwait(false);
            query = ApplyPaging(query, parameters);
        }
        else
        {
            query = query.Take(MaxUnboundedResultLimit);
        }

        // Project LAST: filtering, sorting and paging all run over entity rows, so the provider pages
        // exactly the rows it means to and only that page's columns are selected. Navigation includes
        // are deliberately not applied on this path: the projection itself decides which columns and
        // joins the provider emits, and an Include over a non-entity projection is ignored anyway.
        var result = await queryableExecutor.ToListAsync(project(query), cancellationToken).ConfigureAwait(false);

        if (!isPaginated)
        {
            totalCount = await CountUnpaginatedAsync(unpagedQuery, result.Count, cancellationToken).ConfigureAwait(false);
        }

        return (result, totalCount);
    }

    /// <summary>
    /// Shared front half of the entity path: server-side includes (with the child-collection
    /// split-query switch), specification criteria, and dynamic filters.
    /// </summary>
    private IQueryable<TEntity> ApplyIncludesCriteriaAndFilters<TEntity, TIdentifierType>(
        IQueryable<TEntity> baseQuery,
        NavigationMetadata navigationMetadata,
        EntityQueryParameters<TEntity> parameters)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        var query = baseQuery;

        // PATH 1 - Supported includes: Use EF Core .Include() for navigations where the
        // data source supports JOINs (e.g. SQL Server, SQLite). These translate to SQL
        // and execute server-side.
        if (navigationMetadata.SupportedIncludes.Count != 0)
        {
            foreach (var include in navigationMetadata.SupportedIncludes)
                query = queryableExecutor.Include(query, include.PropertyName);

            // R24/§8: paginating a single-query collection-Include truncates child rows — EF applies
            // Skip/Take to the JOIN-expanded set, so list (GetAll) reads return empty collections while
            // by-id reads (no Skip) work. Force split-query when a child collection is included so each
            // collection loads in its own statement — the documented EF remedy for this shape.
            if (navigationMetadata.SupportedIncludes.Any(nav => nav.Type == NavigationType.ChildCollection))
                query = queryableExecutor.AsSplitQuery(query);
        }

        // Apply specification criteria and dynamic filters BEFORE materializing —
        // this ensures the data source handles as much filtering as possible server-side,
        // avoiding loading the entire entity set for unsupported-include paths.
        if (parameters.Criteria is not null)
            query = query.Where(parameters.Criteria);

        if (parameters.Filters is not null && parameters.Filters.Count != 0)
            query = QueryFilterService.ApplyFilters(query, parameters.Filters, parameters.DTOToEntityPropertyMap);

        return query;
    }

    /// <summary>
    /// PATH 2: Materializes the query, then manually loads related data via the NavigationPopulator.
    /// Sorting and pagination are applied BEFORE materialization so only the required page
    /// is loaded into memory. Navigation population then runs only on the paged subset.
    /// </summary>
    private async Task<(IReadOnlyCollection<TEntity> Items, int TotalCount)> ExecuteWithManualNavigationAsync<TEntity, TIdentifierType>(
        IQueryable<TEntity> query,
        NavigationMetadata navigationMetadata,
        EntityQueryParameters<TEntity> parameters,
        Func<IReadOnlyCollection<TEntity>, NavigationMetadata, bool, bool, CancellationToken, Task> navigationPopulator,
        CancellationToken cancellationToken)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        bool isPaginated = parameters.PageNumber.HasValue && parameters.PageSize.HasValue;
        int totalCount = 0;

        // Sort at the DB level before materialization. A paginated read also gets the key tie-break,
        // so Skip/Take runs over a total order (see PaginationTieBreakProperty).
        query = QueryFieldService.ApplySorting(
            query,
            parameters.SortColumn,
            parameters.SortDirection,
            parameters.DTOToEntityPropertyMap,
            tieBreakProperty: isPaginated ? PaginationTieBreakProperty : null);

        var unpagedQuery = query;

        if (isPaginated)
        {
            totalCount = await queryableExecutor.CountAsync(query, cancellationToken).ConfigureAwait(false);
            query = ApplyPaging(query, parameters);
        }
        else
        {
            // Safety net (rubric §12): an unpaginated query must never materialize an unbounded
            // result set. Cap at the framework ceiling so a caller that omits pagination is bounded.
            query = query.Take(MaxUnboundedResultLimit);
        }

        var entities = await queryableExecutor.ToListAsync(query, cancellationToken).ConfigureAwait(false);
        if (entities.Count != 0)
        {
            await navigationPopulator(entities, navigationMetadata, parameters.IncludeFKs, parameters.IncludeChildren, cancellationToken).ConfigureAwait(false);
        }

        if (!isPaginated)
        {
            totalCount = await CountUnpaginatedAsync(unpagedQuery, entities.Count, cancellationToken).ConfigureAwait(false);
        }

        var pagedQuery = entities.AsQueryable();
        pagedQuery = QueryFieldService.ApplyFieldSelection(pagedQuery, parameters.Fields);
        return (pagedQuery.ToList(), totalCount);
    }

    /// <summary>
    /// PATH 1: All navigations are supported server-side via EF Core .Include().
    /// Applies sorting, pagination, and field selection directly on the IQueryable.
    /// </summary>
    private async Task<(IReadOnlyCollection<TEntity> Items, int TotalCount)> ExecuteWithServerSideIncludesAsync<TEntity, TIdentifierType>(
        IQueryable<TEntity> query,
        EntityQueryParameters<TEntity> parameters,
        CancellationToken cancellationToken)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        bool isPaginated = parameters.PageNumber.HasValue && parameters.PageSize.HasValue;

        // A paginated read also gets the key tie-break, so Skip/Take runs over a total order
        // (see PaginationTieBreakProperty).
        query = QueryFieldService.ApplySorting(
            query,
            parameters.SortColumn,
            parameters.SortDirection,
            parameters.DTOToEntityPropertyMap,
            tieBreakProperty: isPaginated ? PaginationTieBreakProperty : null);

        int totalCount = 0;
        var unpagedQuery = query;

        if (isPaginated)
        {
            // Count must be taken before Skip/Take to get the total matching record count
            totalCount = await queryableExecutor.CountAsync(query, cancellationToken).ConfigureAwait(false);
            query = ApplyPaging(query, parameters);
        }
        else
        {
            // Safety net (rubric §12): an unpaginated query must never materialize an unbounded
            // result set. Cap at the framework ceiling so a caller that omits pagination is bounded.
            query = query.Take(MaxUnboundedResultLimit);
        }

        // Field selection projects only the requested columns (builds a MemberInit expression)
        query = QueryFieldService.ApplyFieldSelection(query, parameters.Fields);
        var result = await queryableExecutor.ToListAsync(query, cancellationToken).ConfigureAwait(false);

        if (!isPaginated)
        {
            totalCount = await CountUnpaginatedAsync(unpagedQuery, result.Count, cancellationToken).ConfigureAwait(false);
        }

        return (result, totalCount);
    }

    /// <summary>
    /// Applies Skip/Take for a paginated request, clamping the page size to
    /// <see cref="MaxUnboundedResultLimit"/> (defense in depth, rubric §12: a direct
    /// Application-layer caller that bypasses the API boundary cannot request an unbounded page).
    /// </summary>
    /// <remarks>
    /// The 64-bit, range-checked offset arithmetic lives in <see cref="PagingMath"/> so every
    /// paginating caller shares it; see that type for why a 32-bit multiply is not safe here.
    /// </remarks>
    private static IQueryable<TEntity> ApplyPaging<TEntity>(
        IQueryable<TEntity> query,
        EntityQueryParameters<TEntity> parameters)
    {
        var (skip, take) = PagingMath.Clamp(
            parameters.PageNumber!.Value,
            parameters.PageSize!.Value,
            MaxUnboundedResultLimit);

        return query.Skip(skip).Take(take);
    }

    /// <summary>
    /// Reports the true total for an unpaginated read. The materialized count is only the truth
    /// while it stays under <see cref="MaxUnboundedResultLimit"/>; at the ceiling it is the cap
    /// itself, and returning it as the total told callers the set was exactly 1000 rows.
    /// </summary>
    private async Task<int> CountUnpaginatedAsync<TEntity>(
        IQueryable<TEntity> unpagedQuery,
        int materializedCount,
        CancellationToken cancellationToken)
        => materializedCount < MaxUnboundedResultLimit
            ? materializedCount
            : await queryableExecutor.CountAsync(unpagedQuery, cancellationToken).ConfigureAwait(false);
}
