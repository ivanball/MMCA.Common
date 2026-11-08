using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Infrastructure.FeatureFlags;

/// <summary>An assembly whose <c>*Features</c> classes declare flags this host may override.</summary>
/// <param name="Assembly">The assembly, typically a module's <c>*.Shared</c> assembly.</param>
internal sealed record FeatureFlagAssembly(Assembly Assembly);

/// <summary>
/// The flags this host declares: <see cref="FeatureFlagRegistry.Describe(IEnumerable{Assembly})"/>
/// over every assembly the host passed to <c>AddReloadableFeatureFlags</c> (each module may add its
/// own). Only these names can carry a stored override.
/// </summary>
/// <param name="assemblies">The registered flag assemblies.</param>
internal sealed class FeatureFlagCatalog(IEnumerable<FeatureFlagAssembly> assemblies)
{
    private readonly Lazy<IReadOnlyList<FeatureFlagDescriptor>> _flags = new(() =>
        [.. FeatureFlagRegistry.Describe(assemblies.Select(a => a.Assembly).Distinct())
            .DistinctBy(flag => flag.FlagName, StringComparer.OrdinalIgnoreCase)]);

    /// <summary>Gets the declared flags, ordered by name.</summary>
    public IReadOnlyList<FeatureFlagDescriptor> Flags => _flags.Value;

    /// <summary>Finds a declared flag by name, case-insensitively like the configuration keys.</summary>
    /// <param name="name">The name to look up.</param>
    /// <param name="flag">The declared flag, with its declared spelling.</param>
    /// <returns><see langword="true"/> when the name is declared.</returns>
    public bool TryFind(string? name, [NotNullWhen(true)] out FeatureFlagDescriptor? flag)
    {
        var trimmed = name?.Trim();
        flag = Flags.FirstOrDefault(f => string.Equals(f.FlagName, trimmed, StringComparison.OrdinalIgnoreCase));
        return flag is not null;
    }
}
