namespace MMCA.Common.Shared.FeatureFlags;

/// <summary>
/// The body of <c>PUT {flags}/{name}</c>: the override to store for one declared feature flag.
/// </summary>
/// <param name="Mode">What the override sets the flag to.</param>
/// <param name="Reason">
/// Why the override exists. Required: an override is deliberately not time-boxed, so its reason is
/// what tells the next operator whether it can be cleared.
/// </param>
/// <param name="Percentage">
/// The rollout percentage (0 to 100). Required for <see cref="FeatureFlagOverrideMode.Percentage"/>;
/// for <see cref="FeatureFlagOverrideMode.Targeting"/> it is the share of everyone outside the named
/// users and groups; ignored otherwise.
/// </param>
/// <param name="Users">The user identifiers a targeting override enables the flag for.</param>
/// <param name="Groups">The groups (roles, tenants) a targeting override enables the flag for.</param>
public sealed record SetFeatureFlagOverrideRequest(
    FeatureFlagOverrideMode Mode,
    string Reason,
    int? Percentage = null,
    IReadOnlyList<string>? Users = null,
    IReadOnlyList<string>? Groups = null);
