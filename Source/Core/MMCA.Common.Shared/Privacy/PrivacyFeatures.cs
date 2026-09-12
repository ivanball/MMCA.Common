using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Shared.Privacy;

/// <summary>
/// Feature flag constants for the privacy (data-subject rights) surface.
/// </summary>
public static class PrivacyFeatures
{
    /// <summary>Feature flag controlling the data-subject export (DSAR) endpoint.</summary>
    [FeatureFlag(FeatureFlagLifetime.Permanent, Owner = "MMCA.Common")]
    public const string DataExport = "Privacy.DataExport";
}
