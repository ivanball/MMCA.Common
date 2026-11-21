using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
/// <b>Keyed by the owning service and data source.</b> Services that share one Redis would otherwise
/// read each other's snapshots; each service owns its overrides in its own database, so both the host's
/// application name (<see cref="IHostEnvironment.ApplicationName"/>) and the source name are part of the
/// key. The source name alone is not enough: two services that keep the default
/// <c>FeatureFlags:DataSourceName</c> on one Redis would share one snapshot, the last loader would win and
/// either service's invalidation would drop the other's.
/// </para>
/// <para>
/// Singleton, and it opens its own DI scope per load because the store is scoped (it shares the
/// owning service's <c>DbContext</c>): the <c>PermissionGrantCache</c> shape.
/// </para>
/// </remarks>
/// <param name="cache">The host's cache service.</param>
/// <param name="scopeFactory">Factory for the scope each load resolves the store from.</param>
/// <param name="settings">Bound override settings (the entry lifetime and the owning source).</param>
/// <param name="environment">
/// The host environment, whose application name identifies the owning service. Optional so a bare
/// container still resolves the snapshot; the entry assembly name stands in when it is absent.
/// </param>
internal sealed class FeatureFlagOverrideSnapshot(
    ICacheService cache,
    IServiceScopeFactory scopeFactory,
    IOptions<FeatureFlagOverrideSettings> settings,
    IHostEnvironment? environment = null) : IFeatureFlagOverrideSnapshot
{
    /// <summary>The root every snapshot key sits under.</summary>
    internal const string KeyRoot = "featureflags:overrides:";

    private readonly TimeSpan _lifetime = TimeSpan.FromSeconds(settings.Value.OverrideCacheSeconds);

    /// <summary>
    /// The invalidation boundary of this service's snapshot (ends in <c>:</c>): the owning service, then
    /// the owning data source. The snapshot key sits directly beneath it, so the key and the prefix a
    /// write invalidates can never disagree.
    /// </summary>
    internal string Prefix { get; } = BuildPrefix(ResolveServiceName(environment), settings.Value.DataSourceName);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeatureFlagOverrideResponse>> GetAsync(CancellationToken cancellationToken = default) =>
        await cache.GetOrCreateAsync(string.Concat(Prefix, "all"), LoadAsync, _lifetime, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task InvalidateAsync(CancellationToken cancellationToken = default) =>
        cache.RemoveByPrefixAsync(Prefix, cancellationToken);

    /// <summary>Composes the invalidation boundary for one service and data source.</summary>
    /// <param name="serviceName">The owning service's identity.</param>
    /// <param name="dataSourceName">The owning data source.</param>
    /// <returns>The prefix, ending in <c>:</c>.</returns>
    internal static string BuildPrefix(string serviceName, string dataSourceName) =>
        string.Concat(KeyRoot, serviceName, ":", dataSourceName, ":");

    /// <summary>
    /// The owning service's identity: the host's application name, else the entry assembly name.
    /// </summary>
    /// <param name="environment">The host environment, when one is registered.</param>
    /// <returns>A stable per-service name.</returns>
    private static string ResolveServiceName(IHostEnvironment? environment)
    {
        var name = environment?.ApplicationName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Assembly.GetEntryAssembly()?.GetName().Name;
        }

        return string.IsNullOrWhiteSpace(name) ? "host" : name;
    }

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
