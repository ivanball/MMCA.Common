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
}
