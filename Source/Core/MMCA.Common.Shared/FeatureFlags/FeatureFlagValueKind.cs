using System.Text.Json.Serialization;

namespace MMCA.Common.Shared.FeatureFlags;

/// <summary>
/// The coarse state a feature-flag definition puts a flag in, as the administration surface reports
/// it. Serialized by name.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FeatureFlagValueKind>))]
public enum FeatureFlagValueKind
{
    /// <summary>Off for everyone, including a flag the configuration never mentions.</summary>
    Off = 0,

    /// <summary>On for everyone.</summary>
    On = 1,

    /// <summary>On for some callers, decided per evaluation by one or more feature filters.</summary>
    Conditional = 2,
}
