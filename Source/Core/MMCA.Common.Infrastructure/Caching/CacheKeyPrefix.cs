using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Namespace applied to every <see cref="MMCA.Common.Application.Interfaces.ICacheService"/> key.
/// <para>
/// Services that share one cache instance also share one keyspace. Nothing stops two of them from
/// choosing the same key for different data, so a read in one service can be served another
/// service's value. Giving each service a prefix (for example <c>"conference:"</c>) separates them.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately applied inside <see cref="DistributedCacheService"/> rather than through
/// <c>RedisCacheOptions.InstanceName</c>. <c>InstanceName</c> is prepended by
/// <c>IDistributedCache</c> below our abstraction, where prefix invalidation cannot see it: the
/// SCAN in <see cref="DistributedCacheService.RemoveByPrefixAsync"/> matches raw Redis keys, so it
/// would look for <c>product:*</c> while the stored keys were <c>svc:product:*</c> and evict
/// nothing, silently. Applying the prefix here keeps get, set, remove and prefix eviction working
/// from the same key shape.
/// </para>
/// <para>
/// Only the distributed cache honors it. <see cref="MemoryCacheService"/> is per-process, so its
/// keyspace is private by construction and a prefix would add nothing.
/// </para>
/// </remarks>
public sealed class CacheKeyPrefixOptions
{
    /// <summary>Configuration section binding to these options.</summary>
    public const string SectionName = "Cache";

    /// <summary>
    /// Prefix prepended to every cache key. Leave it empty to take the framework's per-application
    /// default (<c>"{application namespace}:"</c>, see
    /// <see cref="MMCA.Common.Infrastructure.Configuration.ApplicationNamespace"/>); set it
    /// explicitly only to pin a keyspace two hosts of the same application must share.
    /// </summary>
    /// <remarks>
    /// SEC-Common-53. This used to default to no prefix at all, so two applications sharing one
    /// Redis shared one keyspace for both cache entries and distributed locks, silently.
    /// </remarks>
    public string KeyPrefix { get; init; } = string.Empty;
}

/// <summary>Applies <see cref="CacheKeyPrefixOptions.KeyPrefix"/> to cache keys.</summary>
internal sealed class CacheKeyNamespace(string prefix)
{
    /// <summary>A namespace that leaves keys untouched.</summary>
    public static CacheKeyNamespace None { get; } = new(string.Empty);

    /// <summary>Gets the configured prefix.</summary>
    public string Prefix { get; } = prefix ?? string.Empty;

    /// <summary>Builds the namespace from bound options, tolerating an unregistered section.</summary>
    public static CacheKeyNamespace From(IOptions<CacheKeyPrefixOptions>? options)
    {
        var prefix = options?.Value.KeyPrefix;
        return string.IsNullOrEmpty(prefix) ? None : new CacheKeyNamespace(prefix);
    }

    /// <summary>
    /// Builds the namespace from the container: the configured prefix when a host set one,
    /// otherwise the per-application default derived from
    /// <see cref="Configuration.ApplicationNamespace"/> (SEC-Common-53), so two applications sharing
    /// one Redis never share a keyspace by accident.
    /// </summary>
    /// <param name="serviceProvider">The resolving provider.</param>
    /// <returns>The namespace to qualify cache and lock keys with.</returns>
    public static CacheKeyNamespace From(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var configured = serviceProvider.GetService<IOptions<CacheKeyPrefixOptions>>()?.Value.KeyPrefix;
        if (!string.IsNullOrEmpty(configured))
        {
            return new CacheKeyNamespace(configured);
        }

        var applicationNamespace = Configuration.ApplicationNamespace.Resolve(
            serviceProvider.GetService<IConfiguration>(),
            serviceProvider.GetService<IHostEnvironment>());

        return new CacheKeyNamespace(string.Concat(applicationNamespace, ":"));
    }

    /// <summary>Qualifies a caller-supplied key.</summary>
    public string Qualify(string key) =>
        Prefix.Length == 0 ? key : string.Concat(Prefix, key);
}
