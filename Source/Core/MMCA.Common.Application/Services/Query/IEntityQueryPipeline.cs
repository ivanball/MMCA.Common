using MMCA.Common.Application.Interfaces.Navigation;
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

    /// <summary>
    /// Executes the query pipeline with server-side projection: criteria, dynamic filters, sorting
    /// and pagination all run over entity rows, then <paramref name="project"/> rewrites the query so
    /// the provider returns the projected shape directly. Nothing is materialized as an entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This path exists for reads whose result type has a registered
    /// <c>IEntityDTOProjector</c>. It skips two costs of the entity path: selecting every entity
    /// column, and mapping each materialized row afterwards.
    /// </para>
    /// <para>
    /// It handles server-side navigations only. There is no navigation-populator hook, because a
    /// projection cannot be post-processed row by row, so a query with cross-source
    /// (unsupported) includes must use <see cref="ExecuteAsync"/> instead. Navigation includes are
    /// not applied here either: the projection decides what the provider joins and selects.
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TResult">The projected result type.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
    /// <param name="baseQuery">The starting queryable (tracked or untracked).</param>
    /// <param name="parameters">All query parameters (criteria, filters, sort, pagination).</param>
    /// <param name="project">Rewrites the entity queryable into the projected queryable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The materialized projections and total count for pagination.</returns>
    Task<(IReadOnlyCollection<TResult> Items, int TotalCount)> ExecuteProjectedAsync<TEntity, TResult, TIdentifierType>(
        IQueryable<TEntity> baseQuery,
        EntityQueryParameters<TEntity> parameters,
        Func<IQueryable<TEntity>, IQueryable<TResult>> project,
        CancellationToken cancellationToken)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull;
}
