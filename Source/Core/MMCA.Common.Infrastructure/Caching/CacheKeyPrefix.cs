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
    /// Prefix prepended to every cache key. Empty (the default) keeps keys exactly as callers
    /// write them, which is correct for a host that does not share its cache.
    /// </summary>
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

    /// <summary>Qualifies a caller-supplied key.</summary>
    public string Qualify(string key) =>
        Prefix.Length == 0 ? key : string.Concat(Prefix, key);
}
