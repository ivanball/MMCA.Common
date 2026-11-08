namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// The flag sources the shared feature-flag administration page lists: one API route prefix per
/// service that mounts a <c>FeatureFlagsControllerBase</c>, relative to the API base address (for
/// example <c>Admin/Conference/FeatureFlags</c> and <c>Admin/Engagement/FeatureFlags</c> behind one
/// gateway). Set by <c>AddFeatureFlagAdministrationUI</c>.
/// </summary>
public sealed class FeatureFlagAdminUIOptions
{
    /// <summary>Gets or sets the API route prefixes to list flags from, in display order.</summary>
    public IReadOnlyList<string> Sources { get; set; } = [];
}
