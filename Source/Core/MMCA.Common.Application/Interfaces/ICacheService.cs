using MMCA.Common.Shared.Concurrency;

namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Abstraction over a distributed or in-memory cache, supporting key-based and
/// prefix-based operations. Used by <see cref="UseCases.Decorators.CachingCommandDecorator{TCommand,TResult}"/>
/// for automatic cache invalidation after mutations.
/// </summary>
public interface ICacheService
{
    /// <summary>Retrieves a cached value by key, or <see langword="null"/> if not found.</summary>
    /// <typeparam name="T">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached value, or <see langword="null"/> if the key does not exist.</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores a value in the cache with an optional expiration.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="expiration">Optional sliding or absolute expiration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a single cache entry by exact key.</summary>
    /// <param name="key">The cache key to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes all cache entries whose keys start with the given prefix.</summary>
    /// <param name="prefix">The key prefix to match.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments a counter and returns its new value, setting <paramref name="expiration"/>
    /// when the counter is created. Used by rate-limit and brute-force counters (ADR-029), where the
    /// read-modify-write shape of <see cref="GetAsync{T}"/> + <see cref="SetAsync{T}"/> lets
    /// concurrent requests overwrite each other's increments and undercount.
    /// </summary>
    /// <param name="key">The counter key.</param>
    /// <param name="expiration">Time-to-live applied when the counter is first created.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counter value after the increment.</returns>
    /// <remarks>
    /// The default implementation is the non-atomic read-modify-write, preserving behavior for
    /// implementations with no native counter primitive (and keeping this a non-breaking addition).
    /// Backing stores that can do better (Redis <c>INCR</c>) override it.
    /// </remarks>
    async Task<long> IncrementAsync(string key, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync<long?>(key, cancellationToken).ConfigureAwait(false) ?? 0;
        var next = current + 1;
        await SetAsync(key, next, expiration, cancellationToken).ConfigureAwait(false);
        return next;
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or invokes <paramref name="factory"/>
    /// and caches its result. Concurrent misses on one key run the factory once per process
    /// (stampede protection), the same shape
    /// <see cref="UseCases.Decorators.CachingQueryDecorator{TQuery,TResult}"/> applies to cacheable
    /// queries.
    /// </summary>
    /// <typeparam name="T">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Produces the value when the key is absent.</param>
    /// <param name="expiration">Optional expiration applied to the value the factory produced.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached value, or the value the factory produced.</returns>
    /// <remarks>
    /// <para>
    /// Caching is unconditional: whatever the factory returns is stored, including a failed
    /// <see cref="Shared.Abstractions.Result"/> or a <see langword="null"/>-equivalent value. A
    /// caller that must not cache failures has to keep its own read/execute/write sequence, which
    /// is exactly why the caching decorators do NOT route through this member.
    /// </para>
    /// <para>
    /// Stampede protection is best-effort and per process: the stripe is a process-wide table, so
    /// with several replicas over one shared cache the factory can still run once per replica.
    /// A cluster-wide guarantee would need a distributed lock and is deliberately not attempted.
    /// </para>
    /// <para>
    /// The default implementation is get, then per-key stripe, then a double-check, then factory and
    /// set: correct for every backing store and a non-breaking addition (the
    /// <see cref="IncrementAsync"/> precedent). Implementations with a native two-level primitive
    /// (<c>HybridCacheService</c>) override it.
    /// </para>
    /// </remarks>
    async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // Fast path: no lock on a hit.
        var cached = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
            return cached;

        using (await CacheKeyLocks.Locks.AcquireAsync(key, cancellationToken).ConfigureAwait(false))
        {
            // Double-check: the request that held the stripe has populated the key by now, so the
            // waiters read the fresh entry instead of re-running the factory.
            cached = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
                return cached;

            var created = await factory(cancellationToken).ConfigureAwait(false);
            await SetAsync(key, created, expiration, cancellationToken).ConfigureAwait(false);
            return created;
        }
    }
}

/// <summary>
/// Process-wide per-cache-key locks for the default
/// <see cref="ICacheService.GetOrCreateAsync{T}(string, Func{CancellationToken, Task{T}}, TimeSpan?, CancellationToken)"/>
/// implementation. Kept in a non-generic holder so every closed generic call shares one table
/// (statics on a generic method's declaring type would still be shared, but a holder keeps the
/// table addressable and matches <c>QueryCacheKeyLocks</c>).
/// </summary>
/// <remarks>
/// Striped rather than one semaphore per key, for the reasons documented on
/// <see cref="KeyedSemaphoreStripe"/>: a per-key table forces a choice between dropping the entry
/// on release (two callers then run concurrently) and never dropping it (a parameterized cache key
/// grows the table without bound). Separate from the caching decorator's table on purpose: these
/// are different call sites over different keys, and sharing stripes would only widen the
/// unrelated-key collisions striping already tolerates.
/// </remarks>
internal static class CacheKeyLocks
{
    /// <summary>Fixed-width stripes shared by every caller of the default implementation.</summary>
    internal static readonly KeyedSemaphoreStripe Locks = new();
}
