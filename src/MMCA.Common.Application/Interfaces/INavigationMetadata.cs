namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Classifies navigation properties as either foreign key references or child collections.
/// </summary>
public enum NavigationType
{
    /// <summary>A reference navigation to a parent/related entity (e.g. Product.Category).</summary>
    ForeignKey,

    /// <summary>A collection navigation to child entities (e.g. Order.OrderLines).</summary>
    ChildCollection
}

/// <summary>
/// Metadata about a single navigation property, including the declaring and target entity types.
/// Used by the query pipeline to determine include strategy.
/// </summary>
/// <param name="PropertyName">The CLR property name on the declaring entity.</param>
/// <param name="Type">Whether this is a FK reference or child collection.</param>
/// <param name="DeclaringEntityType">The entity type that declares this navigation property.</param>
/// <param name="TargetEntityType">The related entity type (unwrapped from collection generics).</param>
public sealed record class NavigationPropertyInfo(
    string PropertyName,
    NavigationType Type,
    Type DeclaringEntityType,
    Type TargetEntityType
);

/// <summary>
/// Categorizes navigation properties into supported (EF Core .Include()) and unsupported
/// (manual loading) groups based on data source capabilities.
/// </summary>
public interface INavigationMetadata
{
    /// <summary>Navigations that can be loaded via EF Core <c>.Include()</c> (same data source).</summary>
    IReadOnlyList<NavigationPropertyInfo> SupportedIncludes { get; }

    /// <summary>Navigations that require manual batch loading (cross data source).</summary>
    IReadOnlyList<NavigationPropertyInfo> UnsupportedIncludes { get; }
}
