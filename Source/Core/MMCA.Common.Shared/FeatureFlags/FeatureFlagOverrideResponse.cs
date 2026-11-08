namespace MMCA.Common.Shared.FeatureFlags;

/// <summary>
/// One stored feature-flag override as the administration surface reports it.
/// </summary>
/// <param name="Name">The flag the override applies to.</param>
/// <param name="Mode">What the override sets the flag to.</param>
/// <param name="Percentage">The rollout percentage, when the mode carries one.</param>
/// <param name="Users">The targeted user identifiers; empty unless the mode is targeting.</param>
/// <param name="Groups">The targeted groups; empty unless the mode is targeting.</param>
/// <param name="Reason">Why the override exists.</param>
/// <param name="ChangedOn">The UTC instant the override was last written.</param>
/// <param name="ChangedBy">The user who last wrote it, or the system sentinel.</param>
public sealed record FeatureFlagOverrideResponse(
    string Name,
    FeatureFlagOverrideMode Mode,
    int? Percentage,
    IReadOnlyList<string> Users,
    IReadOnlyList<string> Groups,
    string Reason,
    DateTime? ChangedOn,
    UserIdentifierType? ChangedBy);
