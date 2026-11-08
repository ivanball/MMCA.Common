namespace MMCA.Common.Shared.FeatureFlags;

/// <summary>
/// One declared feature flag as the administration surface lists it: what it is, what configuration
/// says, what (if anything) a stored override says, and which of the two is in force.
/// </summary>
/// <param name="Name">The flag name, e.g. <c>Conference.SessionScoring</c>.</param>
/// <param name="Lifetime">The declared lifetime (ADR-031), or <see langword="null"/> when undeclared.</param>
/// <param name="Owner">The declared owner, or <see langword="null"/>.</param>
/// <param name="RemoveBy">The declared removal date (<c>yyyy-MM-dd</c>), or <see langword="null"/>.</param>
/// <param name="Configured">The state configuration alone puts the flag in.</param>
/// <param name="ConfiguredFilters">The feature filters configuration names, when <paramref name="Configured"/> is conditional.</param>
/// <param name="Override">The stored override, or <see langword="null"/> when configuration is in force.</param>
/// <param name="Effective">The state in force: the override's when there is one, otherwise configuration's.</param>
public sealed record FeatureFlagStatusResponse(
    string Name,
    FeatureFlagLifetime? Lifetime,
    string? Owner,
    string? RemoveBy,
    FeatureFlagValueKind Configured,
    IReadOnlyList<string> ConfiguredFilters,
    FeatureFlagOverrideResponse? Override,
    FeatureFlagValueKind Effective);
