namespace MMCA.Common.Domain.Attributes;

/// <summary>
/// Marks a domain entity property as a navigation for use by <c>NavigationMetadataProvider</c>,
/// which builds metadata for EF include paths and field projection.
/// This decouples navigation discovery from EF Core's own metadata, allowing the
/// application layer to resolve includes without referencing Infrastructure.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class NavigationAttribute : Attribute
{
    /// <summary>
    /// Gets a value indicating whether this navigation is a child collection (one-to-many)
    /// versus a single reference (foreign key / many-to-one).
    /// </summary>
    public bool IsCollection { get; init; }
}
