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
        var query = baseQuery;

        // PATH 1 - Supported includes: Use EF Core .Include() for navigations where the
        // data source supports JOINs (e.g. SQL Server, SQLite). These translate to SQL
        // and execute server-side.
        if (navigationMetadata.SupportedIncludes.Count != 0)
        {
            foreach (var include in navigationMetadata.SupportedIncludes)
                query = queryableExecutor.Include(query, include.PropertyName);
        }

        // Apply specification criteria and dynamic filters BEFORE materializing —
        // this ensures the data source handles as much filtering as possible server-side,
        // avoiding loading the entire entity set for unsupported-include paths.
        if (parameters.Criteria is not null)
            query = query.Where(parameters.Criteria);

        if (parameters.Filters is not null && parameters.Filters.Count != 0)
            query = QueryFilterService.ApplyFilters(query, parameters.Filters, parameters.DTOToEntityPropertyMap);

        // PATH 2 - Unsupported includes: When the data source does not support JOINs
        // (e.g. Cosmos DB where entities may live in different containers), we must
        // materialize the query, then manually load related data via the NavigationPopulator.
        if (navigationMetadata.UnsupportedIncludes.Count != 0)
            return await ExecuteWithManualNavigationAsync<TEntity, TIdentifierType>(query, navigationMetadata, parameters, navigationPopulator, cancellationToken).ConfigureAwait(false);

        return await ExecuteWithServerSideIncludesAsync<TEntity, TIdentifierType>(query, parameters, cancellationToken).ConfigureAwait(false);
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

        // Sort at the DB level before materialization
        query = QueryFieldService.ApplySorting(query, parameters.SortColumn, parameters.SortDirection, parameters.DTOToEntityPropertyMap);

        if (isPaginated)
        {
            totalCount = await queryableExecutor.CountAsync(query, cancellationToken).ConfigureAwait(false);
            int skip = checked(parameters.PageSize!.Value * (parameters.PageNumber!.Value - 1));
            query = query.Skip(skip).Take(parameters.PageSize.Value);
        }

        var entities = await queryableExecutor.ToListAsync(query, cancellationToken).ConfigureAwait(false);
        if (entities.Count != 0)
        {
            await navigationPopulator(entities, navigationMetadata, parameters.IncludeFKs, parameters.IncludeChildren, cancellationToken).ConfigureAwait(false);
        }

        if (!isPaginated)
            totalCount = entities.Count;

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
        query = QueryFieldService.ApplySorting(query, parameters.SortColumn, parameters.SortDirection, parameters.DTOToEntityPropertyMap);

        bool isPaginated = parameters.PageNumber.HasValue && parameters.PageSize.HasValue;
        int totalCount = 0;

        if (isPaginated)
        {
            // Count must be taken before Skip/Take to get the total matching record count
            totalCount = await queryableExecutor.CountAsync(query, cancellationToken).ConfigureAwait(false);
            int skip = checked(parameters.PageSize!.Value * (parameters.PageNumber!.Value - 1));
            query = query.Skip(skip).Take(parameters.PageSize.Value);
        }

        // Field selection projects only the requested columns (builds a MemberInit expression)
        query = QueryFieldService.ApplyFieldSelection(query, parameters.Fields);
        var result = await queryableExecutor.ToListAsync(query, cancellationToken).ConfigureAwait(false);

        if (!isPaginated)
            totalCount = result.Count;

        return (result, totalCount);
    }
}
