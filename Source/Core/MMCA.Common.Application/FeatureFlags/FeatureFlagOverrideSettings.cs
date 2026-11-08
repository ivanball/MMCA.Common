using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Application.FeatureFlags;

/// <summary>
/// Configuration for the reloadable feature flags (stored overrides, ADR-031). Bound from the
/// <c>FeatureFlags</c> configuration section by <c>AddReloadableFeatureFlags</c>.
/// </summary>
public sealed class FeatureFlagOverrideSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "FeatureFlags";

    /// <summary>
    /// How long the cached override snapshot is served before it is reloaded, in seconds.
    /// </summary>
    /// <remarks>
    /// A write through the administration service invalidates the snapshot at once. This lifetime is
    /// the bound on how long ANOTHER replica can keep serving the old snapshot: with a distributed L2
    /// and a shared Redis for the generation tokens, a flip reaches every replica within the local
    /// cache duration; without one, this value is the staleness bound.
    /// </remarks>
    [Range(1, 3600)]
    public int OverrideCacheSeconds { get; init; } = 30;

    /// <summary>
    /// The logical data source that owns the override table: the owning service's own database.
    /// Defaults to the engine's default source, which is right for a single-database host.
    /// </summary>
    [Required]
    public string DataSourceName { get; init; } = "Default";
}
