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
        if (cache.TryGetValue(key, out T? value))
        {
            return Task.FromResult(value);
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

        // Keep _keys in sync: remove the key when evicted (expiration, capacity pressure, or manual removal).
        options.RegisterPostEvictionCallback((evictedKey, _, _, _) =>
            _keys.TryRemove(evictedKey.ToString()!, out _));

        cache.Set(key, value, options);
        _keys.TryAdd(key, 0);

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
