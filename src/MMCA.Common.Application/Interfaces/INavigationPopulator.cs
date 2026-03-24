namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Manually populates navigation properties that cannot be loaded via EF Core <c>.Include()</c>
/// (e.g. cross-container Cosmos DB relationships). Each module provides its own implementation
/// that knows which related entities to load via <see cref="Services.NavigationLoader"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type whose navigations need populating.</typeparam>
public interface INavigationPopulator<in TEntity>
{
    /// <summary>
    /// Batch-loads unsupported navigation properties for the given entities.
    /// </summary>
    /// <param name="entities">The materialized entities to populate.</param>
    /// <param name="navigationMetadata">Metadata describing which navigations need loading.</param>
    /// <param name="includeFKs">Whether FK reference navigations were requested.</param>
    /// <param name="includeChildren">Whether child collection navigations were requested.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when all navigations have been loaded.</returns>
    Task PopulateAsync(
        IReadOnlyCollection<TEntity> entities,
        NavigationMetadata navigationMetadata,
        bool includeFKs,
        bool includeChildren,
        CancellationToken cancellationToken);
}
