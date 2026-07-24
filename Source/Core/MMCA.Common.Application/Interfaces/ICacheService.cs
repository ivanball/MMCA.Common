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
}
