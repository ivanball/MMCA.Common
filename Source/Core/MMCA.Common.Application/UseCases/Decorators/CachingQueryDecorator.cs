using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Concurrency;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that caches query results when the query implements <see cref="IQueryCacheable"/>.
/// On a cache hit the stored result is returned without executing the inner handler.
/// On a cache miss a per-key lock serializes concurrent executions (stampede protection):
/// exactly one request executes the handler and populates the cache; waiters re-check the
/// cache and return the fresh entry instead of re-running the query. Cache keys are shared
/// process-wide, so the lock table lives in a non-generic holder.
/// <para>
/// Populating the cache after a miss is best-effort: the read has already succeeded by that point,
/// so a cache fault is logged at warning level and swallowed rather than failing the query. Only the
/// populate is guarded; the two read paths are left to propagate, and a genuinely cancelled request
/// still surfaces <see cref="OperationCanceledException"/> exactly as the inner handler would.
/// </para>
/// </summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler.</typeparam>
public sealed partial class CachingQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    ICacheService cacheService,
    ILogger<CachingQueryDecorator<TQuery, TResult>> logger) : IQueryHandler<TQuery, TResult>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CachingQueryDecorator{TQuery, TResult}"/> class
    /// without a logger, discarding the cache-populate warnings.
    /// <para>
    /// Exists for source compatibility with consumers that construct the decorator directly (tests
    /// pinned to a released package version). DI never selects it: container resolution prefers the
    /// logger-bearing constructor, so production keeps logging.
    /// </para>
    /// </summary>
    /// <param name="inner">The wrapped query handler.</param>
    /// <param name="cacheService">The cache backing the query results.</param>
    public CachingQueryDecorator(IQueryHandler<TQuery, TResult> inner, ICacheService cacheService)
        : this(inner, cacheService, NullLogger<CachingQueryDecorator<TQuery, TResult>>.Instance)
    {
    }

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
        using (await QueryCacheKeyLocks.Locks.AcquireAsync(cacheable.CacheKey, cancellationToken).ConfigureAwait(false))
        {
            cached = await cacheService.GetAsync<TResult>(cacheable.CacheKey, cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null)
                return cached;

            var result = await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);

            // Only cache non-failure results
            if (result is not Shared.Abstractions.Result { IsFailure: true })
            {
                try
                {
                    await cacheService.SetAsync(cacheable.CacheKey, result, cacheable.CacheDuration, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The read already succeeded, so a cache blip must not fail the query. The
                    // request token is kept, so real cancellation still propagates through the filter.
                    LogCachePopulateFailed(logger, cacheable.CacheKey, typeof(TQuery).Name, ex);
                }
            }

            return result;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Cache populate failed for key '{CacheKey}' after query '{QueryName}' succeeded; the result is returned uncached")]
    private static partial void LogCachePopulateFailed(
        ILogger logger,
        string cacheKey,
        string queryName,
        Exception exception);
}

/// <summary>
/// Process-wide per-cache-key locks for <see cref="CachingQueryDecorator{TQuery, TResult}"/>.
/// Kept in a non-generic holder so every closed generic decorator shares one table
/// (statics on a generic type would be per closed type).
/// </summary>
/// <remarks>
/// <para>
/// Striped rather than one semaphore per key. A per-key table forces a choice between two
/// defects: removing the entry when the last holder releases opens a window where one caller
/// waits on a semaphore no longer in the table while a second creates a fresh one (both then
/// execute concurrently, defeating the lock), and never removing it lets a cache key that embeds
/// request parameters, such as a user id or a filter value, grow the table without bound.
/// </para>
/// <para>
/// The lock is per-process: with multiple app instances over a shared distributed cache
/// (e.g. Redis), stampede protection is best-effort — at most one handler execution per
/// instance, not one cluster-wide. That duplication is harmless (last write wins with equal
/// content); a cluster-wide guarantee would need a distributed lock and is deliberately
/// not attempted here.
/// </para>
/// </remarks>
internal static class QueryCacheKeyLocks
{
    /// <summary>Fixed-width stripes shared by every closed generic decorator.</summary>
    internal static readonly KeyedSemaphoreStripe Locks = new();
}
