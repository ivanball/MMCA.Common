using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// In-process cache backed by <see cref="IMemoryCache"/>. Tracks all active cache keys
/// in a <see cref="ConcurrentDictionary{TKey,TValue}"/> to support
/// <see cref="RemoveByPrefixAsync"/> — a capability <see cref="IMemoryCache"/> lacks natively.
/// </summary>
internal sealed class MemoryCacheService(IMemoryCache cache) : ICacheService
{
    /// <summary>
    /// Tracks active cache keys. <see cref="IMemoryCache"/> has no key enumeration API,
    /// so this dictionary enables prefix-based bulk removal. The byte value is unused (set-like usage).
    /// Keys are automatically removed via the post-eviction callback when entries expire or are evicted.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        // Guard the cast: the generic TryGetValue<T> overload performs an unchecked (T)stored cast and
        // throws InvalidCastException when a key is reused under a different T. Match on the stored object
        // instead so a type mismatch (or a stored null) surfaces as a clean miss.
        if (cache.TryGetValue(key, out var stored) && stored is T typed)
        {
            return Task.FromResult<T?>(typed);
        }

        return Task.FromResult<T?>(default);
    }

    /// <inheritdoc />
    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        MemoryCacheEntryOptions options = new();

        if (expiration.HasValue)
        {
            options.AbsoluteExpirationRelativeToNow = expiration;
        }

        // Keep _keys in sync: remove the key when evicted (expiration, capacity pressure, or manual
        // removal). Replacement is deliberately excluded. IMemoryCache queues post-eviction
        // callbacks to the thread pool, so overwriting a live key fires the OLD entry's callback
        // asynchronously; it could land after the TryAdd below and delete the tracking record for
        // the entry that just replaced it. The entry would then be live in the cache but invisible
        // to RemoveByPrefixAsync, leaving a stale value that only its TTL could clear.
        options.RegisterPostEvictionCallback((evictedKey, _, reason, _) =>
        {
            if (reason != EvictionReason.Replaced)
                _keys.TryRemove(evictedKey.ToString()!, out _);
        });

        // Track before writing so a concurrent RemoveByPrefixAsync cannot observe a cached entry
        // that is not yet in the table.
        _keys.TryAdd(key, 0);
        cache.Set(key, value, options);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cache.Remove(key);
        _keys.TryRemove(key, out _);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        foreach (var key in _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            cache.Remove(key);
            _keys.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }
}
