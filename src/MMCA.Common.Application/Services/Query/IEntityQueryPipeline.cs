using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.Services.Query;

/// <summary>
/// Orchestrates the multi-step query execution pipeline: navigation includes, criteria,
/// dynamic filters, sorting, pagination, and field projection.
/// </summary>
public interface IEntityQueryPipeline
{
    /// <summary>
    /// Executes the full query pipeline against the base queryable.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
    /// <param name="baseQuery">The starting queryable (tracked or untracked).</param>
    /// <param name="navigationMetadata">Supported and unsupported navigation includes.</param>
    /// <param name="parameters">All query parameters (criteria, filters, sort, pagination, fields).</param>
    /// <param name="navigationPopulator">Callback for manually loading unsupported navigations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The materialized entities and total count for pagination.</returns>
    Task<(IReadOnlyCollection<TEntity> Items, int TotalCount)> ExecuteAsync<TEntity, TIdentifierType>(
        IQueryable<TEntity> baseQuery,
        NavigationMetadata navigationMetadata,
        EntityQueryParameters<TEntity> parameters,
        Func<IReadOnlyCollection<TEntity>, NavigationMetadata, bool, bool, CancellationToken, Task> navigationPopulator,
        CancellationToken cancellationToken)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull;
}
