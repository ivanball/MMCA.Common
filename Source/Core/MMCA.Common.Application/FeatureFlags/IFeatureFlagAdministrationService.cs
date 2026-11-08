using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Application.FeatureFlags;

/// <summary>
/// The administration surface of the reloadable feature flags (ADR-031): list the flags this service
/// declares, and set or clear their stored overrides. Registered by <c>AddReloadableFeatureFlags</c>
/// and served by <c>FeatureFlagsControllerBase</c>.
/// </summary>
/// <remarks>
/// Only a declared flag can be overridden. The declared set is what
/// <c>FeatureFlagRegistry.Describe</c> reports over the assemblies the host passed to
/// <c>AddReloadableFeatureFlags</c>, so an override cannot be written for a name no code reads: a typo
/// is refused with <c>FeatureFlag.Undeclared</c> rather than stored and left silently inert.
/// </remarks>
public interface IFeatureFlagAdministrationService
{
    /// <summary>Lists every declared flag with its configured value, its override and its effective value.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flags, ordered by name.</returns>
    Task<Result<IReadOnlyList<FeatureFlagStatusResponse>>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the stored override of one declared flag and invalidates the cached snapshot.</summary>
    /// <param name="name">The flag name.</param>
    /// <param name="request">The override to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flag's state afterwards, or a failure (an undeclared name, an invalid override).</returns>
    Task<Result<FeatureFlagStatusResponse>> SetOverrideAsync(
        string name,
        SetFeatureFlagOverrideRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the stored override of one declared flag and invalidates the cached snapshot.</summary>
    /// <param name="name">The flag name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or a failure for an undeclared name.</returns>
    Task<Result> ClearOverrideAsync(string name, CancellationToken cancellationToken = default);
}
