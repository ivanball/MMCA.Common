namespace MMCA.Common.Shared.FeatureFlags;

/// <summary>
/// How long a feature flag is meant to exist. The distinction is what makes a stale toggle
/// detectable: a flag that is always going to be there is a configuration switch, while a flag that
/// was only ever meant to cover a rollout becomes dead code the moment the rollout is over, and
/// nothing in the codebase notices unless the flag says so (ADR-031).
/// </summary>
public enum FeatureFlagLifetime
{
    /// <summary>
    /// A flag with no end date: a capability a host chooses to run with or without (push
    /// notifications, a data-subject export endpoint). It must NOT carry a removal date.
    /// </summary>
    Permanent,

    /// <summary>
    /// A flag that exists only until a migration, rollout or experiment finishes. It must carry a
    /// removal date, and the build fails once that date has passed.
    /// </summary>
    Temporary,
}
