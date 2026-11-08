using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Application.FeatureFlags;

/// <summary>
/// The persistence of stored feature-flag overrides (ADR-031). The shipped implementation is the EF
/// store <c>AddReloadableFeatureFlags</c> registers over the owning service's own database.
/// </summary>
/// <remarks>
/// The store does not check that a name is a declared flag; the administration service does, before
/// it calls the store. Writes here do not invalidate the cached snapshot either: the administration
/// service does that after each successful write.
/// </remarks>
public interface IFeatureFlagOverrideStore
{
    /// <summary>Reads every active override, ordered by flag name.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active overrides; cleared ones are not included.</returns>
    Task<IReadOnlyList<FeatureFlagOverrideResponse>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the override of one flag.</summary>
    /// <param name="name">The flag name, as declared.</param>
    /// <param name="request">The override to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored override, or a validation failure.</returns>
    Task<Result<FeatureFlagOverrideResponse>> SetAsync(
        string name,
        SetFeatureFlagOverrideRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the override of one flag. Clearing a flag with no override succeeds.</summary>
    /// <param name="name">The flag name, as declared.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success.</returns>
    Task<Result> ClearAsync(string name, CancellationToken cancellationToken = default);
}
