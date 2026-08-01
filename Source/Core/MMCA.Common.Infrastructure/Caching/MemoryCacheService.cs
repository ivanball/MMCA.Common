using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Concurrency;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// In-process cache backed by <see cref="IMemoryCache"/>. Tracks all active cache keys
/// in a <see cref="ConcurrentDictionary{TKey,TValue}"/> to support
/// <see cref="RemoveByPrefixAsync"/>, a capability <see cref="IMemoryCache"/> lacks natively.
/// <para>
/// The cache and the tracking table are two structures that have to agree, so every mutation of a
/// key takes that key's stripe from <see cref="KeyedSemaphoreStripe"/> and touches the cache before
/// the table. See the invariant note on <see cref="SetAsync{T}"/>.
/// </para>
/// </summary>
internal sealed class MemoryCacheService(IMemoryCache cache) : ICacheService
{
    /// <summary>
    /// Tracks active cache keys. <see cref="IMemoryCache"/> has no key enumeration API,
    /// so this dictionary enables prefix-based bulk removal. Keys are removed here again by the
    /// post-eviction callback when entries expire or are evicted.
    /// <para>
    /// The value is the tracking token of the cache entry the record belongs to: a plain object
    /// compared by reference. It exists so a post-eviction callback, which runs asynchronously and
    /// therefore after its entry may already have been superseded, can remove only its OWN record
    /// and never the record of a newer live entry.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _keys = new(StringComparer.Ordinal);

    /// <summary>
    /// Serializes the paired mutation of the cache and <c>_keys</c> for one key. Per instance rather
    /// than static: the tracking table belongs to this service instance, so two instances have
    /// nothing to serialize against each other.
    /// </summary>
    private readonly KeyedSemaphoreStripe _keyLocks = new();

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
    /// <remarks>
    /// Establishes the invariant the whole class rests on: the cache entry and its tracking record
    /// are written under the key's stripe, cache first and <c>_keys</c> second, and every other
    /// mutating member uses that same lock and that same order. Ordering alone cannot fix this.
    /// Track-then-write lets a concurrent removal drop the tracking record between the two steps and
    /// leave a live entry nothing can find; write-then-track lets a removal run entirely between
    /// them and leave the same orphan. Only mutual exclusion removes the window, after which no
    /// observer can see a cached entry that <c>_keys</c> does not track.
    /// </remarks>
    public async Task SetAsync<T>(
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

        // Identity of THIS entry's tracking record, handed to the callback as its state.
        var token = new object();

        // Keep _keys in sync: remove the key when evicted (expiration, capacity pressure, or manual
        // removal). This callback stays deliberately LOCK-FREE. IMemoryCache queues it to the thread
        // pool, and waiting on a stripe from a pool thread would stall the pool behind whichever
        // caller holds it.
        //
        // It therefore runs after the fact, when the key may already carry a NEWER live entry, so it
        // removes the record only while the record is still its own (token compared by reference).
        // Untracking a live entry is the state that must never happen: the entry stays in the cache
        // but is invisible to RemoveByPrefixAsync, a stale value only its TTL could clear.
        //
        // Replacement keeps its own exclusion in front of that check. Overwriting a live key fires
        // the OLD entry's callback, and the token for the replacement is published a moment later,
        // so the cheap reason test settles the common case without depending on the ordering at all.
        options.RegisterPostEvictionCallback(
            (evictedKey, _, reason, state) =>
            {
                if (reason != EvictionReason.Replaced)
                    _keys.TryRemove(new KeyValuePair<string, object>(evictedKey.ToString()!, state!));
            },
            token);

        using (await _keyLocks.AcquireAsync(key, cancellationToken).ConfigureAwait(false))
        {
            cache.Set(key, value, options);
            _keys[key] = token;
        }
    }

    /// <inheritdoc />
    /// <remarks>Same stripe and same order as <see cref="SetAsync{T}"/>: cache, then <c>_keys</c>.</remarks>
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        using (await _keyLocks.AcquireAsync(key, cancellationToken).ConfigureAwait(false))
        {
            cache.Remove(key);
            _keys.TryRemove(key, out _);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The candidate list is a snapshot (<see cref="ConcurrentDictionary{TKey,TValue}.Keys"/> already
    /// copies), so it is enumerated outside every lock. Each key is then removed under its own stripe,
    /// one at a time, and the stripe is released before the next one is taken. Handles are never
    /// accumulated across the loop: distinct keys can map to the same stripe and to different stripes
    /// in a different relative order, so holding several at once would let two prefix removals block
    /// on each other's stripes and deadlock.
    /// </remarks>
    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        foreach (var key in _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using (await _keyLocks.AcquireAsync(key, cancellationToken).ConfigureAwait(false))
            {
                cache.Remove(key);
                _keys.TryRemove(key, out _);
            }
        }
    }
}
