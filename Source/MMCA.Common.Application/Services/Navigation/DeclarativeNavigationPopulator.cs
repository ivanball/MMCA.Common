using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Application.Services.Navigation;

/// <summary>
/// Generic navigation populator driven by a list of <see cref="INavigationDescriptor{TEntity}"/>
/// declarations. Eliminates the per-entity boilerplate of manually checking which navigations
/// to load and calling <see cref="NavigationLoader"/> for each one.
/// </summary>
/// <typeparam name="TEntity">The entity type whose navigations need populating.</typeparam>
/// <param name="unitOfWork">Unit of work for repository access.</param>
/// <param name="descriptors">The navigation descriptors defining what to load.</param>
public class DeclarativeNavigationPopulator<TEntity>(
    IUnitOfWork unitOfWork,
    IReadOnlyList<INavigationDescriptor<TEntity>> descriptors)
    : INavigationPopulator<TEntity>
{
    /// <inheritdoc />
    public async Task PopulateAsync(
        IReadOnlyCollection<TEntity> entities,
        NavigationMetadata navigationMetadata,
        bool includeFKs,
        bool includeChildren,
        CancellationToken cancellationToken)
    {
        if (entities.Count == 0 || navigationMetadata.UnsupportedIncludes.Count == 0)
            return;

        var unsupportedPropertyNames = new HashSet<string>(
            navigationMetadata.UnsupportedIncludes.Select(i => i.PropertyName),
            StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
            var shouldLoad = descriptor.RequiresChildren ? includeChildren : includeFKs;
            if (shouldLoad && unsupportedPropertyNames.Contains(descriptor.PropertyName))
            {
                await descriptor.LoadAsync(entities, unitOfWork, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
