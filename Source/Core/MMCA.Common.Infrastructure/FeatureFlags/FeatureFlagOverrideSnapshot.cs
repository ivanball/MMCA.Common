using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Infrastructure.FeatureFlags;

/// <summary>
/// The shipped <see cref="IFeatureFlagOverrideSnapshot"/>: the whole override table as ONE cache entry,
/// held in the host's <see cref="ICacheService"/> under a key that the generation tokens of the
/// two-level cache address, and invalidated by replacing the token of its prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the cache service rather than a private memory cache.</b> On a host that registers the
/// two-level cache (<c>AddCommonHybridCache</c>), the entry lives in <c>HybridCache</c> and its key
/// carries the <c>CachePrefixGenerations</c> tokens of its boundaries, so the invalidation a write
/// performs (<see cref="ICacheService.RemoveByPrefixAsync"/>) replaces one token on the shared Redis and
/// every replica of the service stops addressing the old snapshot within its local cache duration.
/// On a memory-only host the same calls stay in process and the entry lifetime
/// (<c>FeatureFlags:OverrideCacheSeconds</c>) is the staleness bound for the other replicas.
/// </para>
/// <para>
/// <b>Keyed by the owning data source.</b> Services that share one Redis without a key prefix would
/// otherwise read each other's snapshots; each service owns its overrides in its own database, so the
/// source name is part of the key.
/// </para>
/// <para>
/// Singleton, and it opens its own DI scope per load because the store is scoped (it shares the
/// owning service's <c>DbContext</c>): the <c>PermissionGrantCache</c> shape.
/// </para>
/// </remarks>
/// <param name="cache">The host's cache service.</param>
/// <param name="scopeFactory">Factory for the scope each load resolves the store from.</param>
/// <param name="settings">Bound override settings (the entry lifetime and the owning source).</param>
internal sealed class FeatureFlagOverrideSnapshot(
    ICacheService cache,
    IServiceScopeFactory scopeFactory,
    IOptions<FeatureFlagOverrideSettings> settings) : IFeatureFlagOverrideSnapshot
{
    /// <summary>The root every snapshot key sits under.</summary>
    internal const string KeyRoot = "featureflags:overrides:";

    private readonly TimeSpan _lifetime = TimeSpan.FromSeconds(settings.Value.OverrideCacheSeconds);

    /// <summary>The invalidation boundary of this service's snapshot (ends in <c>:</c>).</summary>
    private readonly string _prefix = string.Concat(KeyRoot, settings.Value.DataSourceName, ":");

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeatureFlagOverrideResponse>> GetAsync(CancellationToken cancellationToken = default) =>
        await cache.GetOrCreateAsync(string.Concat(_prefix, "all"), LoadAsync, _lifetime, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task InvalidateAsync(CancellationToken cancellationToken = default) =>
        cache.RemoveByPrefixAsync(_prefix, cancellationToken);

    /// <summary>Reads the whole table through a fresh scope's store.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every active override, as an array so the two-level cache can serialize it.</returns>
    private async Task<FeatureFlagOverrideResponse[]> LoadAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IFeatureFlagOverrideStore>();
            return [.. await store.GetAllAsync(cancellationToken).ConfigureAwait(false)];
        }
    }
}
