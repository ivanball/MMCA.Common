using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Application.FeatureFlags;

/// <summary>
/// The cached whole-table snapshot of the stored feature-flag overrides that every flag evaluation
/// reads (ADR-031), and the invalidation every write performs.
/// </summary>
/// <remarks>
/// One snapshot rather than a lookup per flag, for the reason the permission-grant cache gives: a
/// flag check sits on the request path, and loading every override in one query on a slow interval,
/// and again right after an edit, keeps that check a cache read.
/// </remarks>
public interface IFeatureFlagOverrideSnapshot
{
    /// <summary>Reads the active overrides, from the cache when it holds them.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active overrides.</returns>
    Task<IReadOnlyList<FeatureFlagOverrideResponse>> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops the cached snapshot, so the next read reloads it from the store.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}
