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
        // Optimization: apply sorting and pagination BEFORE materialization (on the DB query)
        // so only the required page is loaded into memory. Navigation population then runs
        // only on the paged subset.
        if (navigationMetadata.UnsupportedIncludes.Count != 0)
        {
            bool isPaginatedPath2 = parameters.PageNumber.HasValue && parameters.PageSize.HasValue;
            int totalCountPath2 = 0;

            // Sort at the DB level before materialization
            query = QueryFieldService.ApplySorting(query, parameters.SortColumn, parameters.SortDirection, parameters.DTOToEntityPropertyMap);

            if (isPaginatedPath2)
            {
                totalCountPath2 = await queryableExecutor.CountAsync(query, cancellationToken).ConfigureAwait(false);
                int skip = checked(parameters.PageSize!.Value * (parameters.PageNumber!.Value - 1));
                query = query.Skip(skip).Take(parameters.PageSize.Value);
            }

            var entities = await queryableExecutor.ToListAsync(query, cancellationToken).ConfigureAwait(false);
            if (entities.Count != 0)
            {
                await navigationPopulator(entities, navigationMetadata, parameters.IncludeFKs, parameters.IncludeChildren, cancellationToken).ConfigureAwait(false);
            }

            if (!isPaginatedPath2)
                totalCountPath2 = entities.Count;

            // Field selection and return — skip the normal path below
            var pagedQuery = entities.AsQueryable();
            pagedQuery = QueryFieldService.ApplyFieldSelection(pagedQuery, parameters.Fields);
            return (pagedQuery.ToList(), totalCountPath2);
        }

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
