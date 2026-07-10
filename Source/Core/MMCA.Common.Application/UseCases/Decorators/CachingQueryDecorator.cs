using System.Collections.Concurrent;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that caches query results when the query implements <see cref="IQueryCacheable"/>.
/// On a cache hit the stored result is returned without executing the inner handler.
/// On a cache miss a per-key lock serializes concurrent executions (stampede protection):
/// exactly one request executes the handler and populates the cache; waiters re-check the
/// cache and return the fresh entry instead of re-running the query. Cache keys are shared
/// process-wide, so the lock table lives in a non-generic holder.
/// </summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler.</typeparam>
public sealed class CachingQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    ICacheService cacheService) : IQueryHandler<TQuery, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default)
    {
        if (query is not IQueryCacheable cacheable)
            return await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        // Fast path: no lock on a hit.
        var cached = await cacheService.GetAsync<TResult>(cacheable.CacheKey, cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
            return cached;

        // Slow path: per-key double-check locking (same pattern as IdempotencyFilter). On
        // expiry of a hot key only one concurrent request runs the handler; the rest wait
        // and are served the freshly cached entry.
        var keyLock = QueryCacheKeyLocks.Locks.GetOrAdd(cacheable.CacheKey, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = await cacheService.GetAsync<TResult>(cacheable.CacheKey, cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null)
                return cached;

            var result = await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);

            // Only cache non-failure results
            if (result is not Shared.Abstractions.Result { IsFailure: true })
            {
                await cacheService.SetAsync(cacheable.CacheKey, result, cacheable.CacheDuration, cancellationToken)
                    .ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            keyLock.Release();

            // Eagerly remove the semaphore when no other requests are waiting on it
            if (keyLock.CurrentCount == 1)
                QueryCacheKeyLocks.Locks.TryRemove(cacheable.CacheKey, out _);
        }
    }
}

/// <summary>
/// Process-wide per-cache-key locks for <see cref="CachingQueryDecorator{TQuery, TResult}"/>.
/// Kept in a non-generic holder so every closed generic decorator shares one table
/// (statics on a generic type would be per closed type).
/// </summary>
internal static class QueryCacheKeyLocks
{
    /// <summary>Per-key semaphores; entries are removed when no waiters remain.</summary>
    internal static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
}
