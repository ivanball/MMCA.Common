using System.Collections.Concurrent;
using System.Reflection;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Attributes;

namespace MMCA.Common.Application.Services.Query;

/// <summary>
/// Discovers navigation properties on entity types via <see cref="NavigationAttribute"/>
/// reflection and classifies them as "supported" or "unsupported" includes based on
/// the underlying data source's JOIN capabilities.
/// <para>
/// <b>Supported includes</b> can be handled by EF Core's <c>.Include()</c> (e.g. SQL Server
/// where both entities share the same database). <b>Unsupported includes</b> require manual
/// loading via <see cref="NavigationLoader"/> (e.g. cross-container Cosmos DB relationships).
/// </para>
/// Results are cached per (entity type, navigation type) to avoid repeated reflection.
/// </summary>
public sealed class NavigationMetadataProvider(IDataSourceService dataSourceService) : INavigationMetadataProvider
{
    /// <summary>
    /// Caches navigation metadata per entity type and navigation kind (FK vs child collection).
    /// Since entity type metadata is immutable, this cache never needs invalidation.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type EntityType, NavigationType NavType), NavigationMetadata> Cache = new();

    /// <inheritdoc />
    public NavigationMetadata BuildIncludes<TEntity>(bool includeFKs, bool includeChildren)
    {
        var navigationMetadata = new NavigationMetadata();

        if (includeFKs)
        {
            var fkMetadata = GetNavigationProperties(typeof(TEntity), NavigationType.ForeignKey);
            navigationMetadata.AddSupportedRange(fkMetadata.SupportedIncludes);
            navigationMetadata.AddUnsupportedRange(fkMetadata.UnsupportedIncludes);
        }

        if (includeChildren)
        {
            var childMetadata = GetNavigationProperties(typeof(TEntity), NavigationType.ChildCollection);
            navigationMetadata.AddSupportedRange(childMetadata.SupportedIncludes);
            navigationMetadata.AddUnsupportedRange(childMetadata.UnsupportedIncludes);
        }

        return navigationMetadata;
    }

    private NavigationMetadata GetNavigationProperties(Type entityType, NavigationType navigationType) =>
        Cache.GetOrAdd((entityType, navigationType), key => BuildNavigationMetadata(key.EntityType, key.NavType));

    /// <summary>
    /// Reflects over the entity's public properties looking for <see cref="NavigationAttribute"/>,
    /// then checks each navigation pair against the data source service's include support check
    /// to determine whether EF Core can handle the include or if manual loading is needed.
    /// </summary>
    private NavigationMetadata BuildNavigationMetadata(Type entityType, NavigationType navigationType)
    {
        var navigationMetadata = new NavigationMetadata();

        foreach (var property in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            ClassifyNavigationProperty(property, navigationType, navigationMetadata);
        }

        return navigationMetadata;
    }

    private void ClassifyNavigationProperty(PropertyInfo property, NavigationType navigationType, NavigationMetadata metadata)
    {
        var navigationAttribute = property.GetCustomAttribute<NavigationAttribute>();
        if (navigationAttribute is null)
            return;

        var attributeNavigationType = navigationAttribute.IsCollection ? NavigationType.ChildCollection : NavigationType.ForeignKey;
        if (attributeNavigationType != navigationType)
            return;

        var declaringEntityType = property.DeclaringType;
        if (declaringEntityType?.FullName is null)
            return;

        var targetEntityType = UnwrapCollectionType(property.PropertyType);
        if (targetEntityType?.FullName is null)
            return;

        var navigationPropertyInfo = new NavigationPropertyInfo(
            property.Name,
            attributeNavigationType,
            declaringEntityType,
            targetEntityType);

        if (dataSourceService.HaveIncludeSupport(declaringEntityType.FullName, targetEntityType.FullName))
            metadata.AddSupported(navigationPropertyInfo);
        else
            metadata.AddUnsupported(navigationPropertyInfo);
    }

    /// <summary>
    /// Unwraps generic collection types (<see cref="ICollection{T}"/>, <see cref="IReadOnlyCollection{T}"/>)
    /// to get the actual target entity type for data source compatibility checks.
    /// </summary>
    private static Type UnwrapCollectionType(Type propertyType)
    {
        if (propertyType.IsGenericType &&
            (propertyType.GetGenericTypeDefinition() == typeof(ICollection<>) ||
             propertyType.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)))
        {
            return propertyType.GenericTypeArguments[0];
        }

        return propertyType;
    }
}
