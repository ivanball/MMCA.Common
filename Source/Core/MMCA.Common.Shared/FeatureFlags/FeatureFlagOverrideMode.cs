using System.Text.Json.Serialization;

namespace MMCA.Common.Shared.FeatureFlags;

/// <summary>
/// What a stored feature-flag override sets a flag to (ADR-031). Serialized by name, so a client reads
/// a newer mode as an unknown string rather than a bare number.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FeatureFlagOverrideMode>))]
public enum FeatureFlagOverrideMode
{
    /// <summary>The flag is off for everyone: the kill switch.</summary>
    Off = 0,

    /// <summary>The flag is on for everyone.</summary>
    On = 1,

    /// <summary>
    /// The flag is on for a stable percentage of users. It runs through the targeting filter's default
    /// rollout, so one user keeps the same answer from request to request.
    /// </summary>
    Percentage = 2,

    /// <summary>
    /// The flag is on for the named users and groups, plus an optional percentage of everyone else.
    /// Groups are what a tenant-wide override is expressed with.
    /// </summary>
    Targeting = 3,
}
