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

        // PATH 2 - Unsupported includes: When the data source does not support JOINs
        // (e.g. Cosmos DB where entities may live in different containers), we must
        // materialize the query early, then manually load related data via the
        // NavigationPopulator. All subsequent filtering/sorting runs in-memory on the
        // materialized collection.
        if (navigationMetadata.UnsupportedIncludes.Count != 0)
        {
            var entities = await queryableExecutor.ToListAsync(query, cancellationToken).ConfigureAwait(false);
            if (entities.Count != 0)
            {
                await navigationPopulator(entities, navigationMetadata, parameters.IncludeFKs, parameters.IncludeChildren, cancellationToken).ConfigureAwait(false);
            }

            query = entities.AsQueryable();
        }

        // Apply specification criteria (e.g. authorization-based filtering)
        if (parameters.Criteria is not null)
            query = query.Where(parameters.Criteria);

        // Apply dynamic user-supplied filters
        if (parameters.Filters is not null && parameters.Filters.Count != 0)
            query = QueryFilterService.ApplyFilters(query, parameters.Filters, parameters.DTOToEntityPropertyMap);

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
