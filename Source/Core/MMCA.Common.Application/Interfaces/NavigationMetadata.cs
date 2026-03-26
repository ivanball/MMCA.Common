namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Mutable builder for <see cref="INavigationMetadata"/>. Populated by
/// <see cref="Services.Query.NavigationMetadataProvider"/> and consumed by
/// <see cref="Services.Query.EntityQueryPipeline"/> to determine which navigations
/// use EF Core includes vs manual batch loading.
/// </summary>
public sealed class NavigationMetadata : INavigationMetadata
{
    private readonly List<NavigationPropertyInfo> _supportedIncludes = [];
    private readonly List<NavigationPropertyInfo> _unsupportedIncludes = [];

    /// <inheritdoc />
    public IReadOnlyList<NavigationPropertyInfo> SupportedIncludes => _supportedIncludes;

    /// <inheritdoc />
    public IReadOnlyList<NavigationPropertyInfo> UnsupportedIncludes => _unsupportedIncludes;

    /// <summary>Adds a navigation property to the supported (EF Core include) list.</summary>
    /// <param name="info">The navigation property metadata.</param>
    internal void AddSupported(NavigationPropertyInfo info) => _supportedIncludes.Add(info);

    /// <summary>Adds a navigation property to the unsupported (manual loading) list.</summary>
    /// <param name="info">The navigation property metadata.</param>
    internal void AddUnsupported(NavigationPropertyInfo info) => _unsupportedIncludes.Add(info);

    /// <summary>Adds multiple navigation properties to the supported list.</summary>
    /// <param name="infos">The navigation property metadata items.</param>
    internal void AddSupportedRange(IEnumerable<NavigationPropertyInfo> infos) => _supportedIncludes.AddRange(infos);

    /// <summary>Adds multiple navigation properties to the unsupported list.</summary>
    /// <param name="infos">The navigation property metadata items.</param>
    internal void AddUnsupportedRange(IEnumerable<NavigationPropertyInfo> infos) => _unsupportedIncludes.AddRange(infos);
}
