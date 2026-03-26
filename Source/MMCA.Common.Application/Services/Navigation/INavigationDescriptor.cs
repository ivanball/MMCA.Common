using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Application.Services.Navigation;

/// <summary>
/// Describes a single navigation property that needs manual loading. Used by
/// <see cref="DeclarativeNavigationPopulator{TEntity}"/> to eliminate per-entity boilerplate.
/// </summary>
/// <typeparam name="TEntity">The parent entity type.</typeparam>
public interface INavigationDescriptor<in TEntity>
{
    /// <summary>Gets the navigation property name (must match the EF Core property name).</summary>
    string PropertyName { get; }

    /// <summary>
    /// Gets whether this navigation requires <c>includeChildren</c> (<see langword="true"/>)
    /// or <c>includeFKs</c> (<see langword="false"/>) to be loaded.
    /// </summary>
    bool RequiresChildren { get; }

    /// <summary>
    /// Batch-loads the navigation property for all parent entities in a single query.
    /// </summary>
    Task LoadAsync(
        IReadOnlyCollection<TEntity> entities,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken);
}
