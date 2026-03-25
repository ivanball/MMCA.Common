using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.Services;

/// <summary>
/// No-op implementation of <see cref="INavigationPopulator{TEntity}"/> for entities
/// that have no unsupported navigation properties requiring manual loading.
/// Registered as the default when a module does not provide a custom populator.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class NullNavigationPopulator<TEntity> : INavigationPopulator<TEntity>
{
    /// <inheritdoc />
    public Task PopulateAsync(
        IReadOnlyCollection<TEntity> entities,
        NavigationMetadata navigationMetadata,
        bool includeFKs,
        bool includeChildren,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
