using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Settings;
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
/// Every cache call is fail-open: the cache is an optimization, never the system of record, so a
/// cache outage must degrade the application to uncached reads rather than turn every cacheable
/// query into a 500. Both reads and the populate log at warning level and swallow the fault. A
/// failed read is treated as a miss and falls through to the inner handler, so the query still
/// answers correctly, just uncached; a failed populate returns the handler's result uncached. Only
/// <see cref="OperationCanceledException"/> is excluded from the guard, so a genuinely cancelled
/// request still surfaces exactly as the inner handler would.
/// </para>
/// <para>
/// <b>Tenant isolation lives here, not in the cache.</b> <c>ICacheService</c> is a singleton and
/// cannot see the scoped tenant, so a cache key that two tenants both compute would serve one
/// tenant's rows to the other. When a tenant is resolved the decorator prefixes the key (and the
/// stampede lock) with <c>t:{tenantId}:</c>; when none is, keys are exactly what they were before
/// tenancy shipped.
/// </para>
/// <para>
/// The wait on that stampede lock is bounded by
/// <see cref="QueryCachePipelineSettings.PopulateLockTimeout"/> (<c>Cache:PopulateLockTimeout</c>),
/// which is unbounded by default. A waiter that gives up is fail-open like every other cache
/// failure here: it runs the inner handler and returns its result uncached.
/// </para>
/// </summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler.</typeparam>
public sealed partial class CachingQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    ICacheService cacheService,
    ILogger<CachingQueryDecorator<TQuery, TResult>> logger,
    ITenantContext? tenantContext = null,
    IOptions<QueryCachePipelineSettings>? pipelineSettings = null) : IQueryHandler<TQuery, TResult>
{
    /// <summary>
    /// The cache key for this query in the current tenant: the query's own key, prefixed with the
    /// tenant when one is resolved. Two tenants therefore never share an entry, and a host that
    /// resolves no tenant keeps byte-identical keys to the pre-tenancy framework.
    /// </summary>
    /// <param name="cacheable">The cacheable query carrying the key.</param>
    /// <returns>The effective cache key.</returns>
    private string EffectiveKey(IQueryCacheable cacheable) =>
        TenantCacheKey.Scope(tenantContext, cacheable.CacheKey);

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default)
    {
        if (query is not IQueryCacheable cacheable)
            return await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        var queryName = typeof(TQuery).Name;

        // Tenant-scoped from here down: read, stampede lock and populate must all agree, or one
        // tenant would wait on another's lock and read another's entry.
        var cacheKey = EffectiveKey(cacheable);

        // Fast path: no lock on a hit.
        var cached = await TryReadAsync(cacheKey, queryName, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            CqrsMetrics.RecordCacheHit(queryName);
            return cached;
        }

        // Slow path: per-key double-check locking (same pattern as IdempotencyFilter). On
        // expiry of a hot key only one concurrent request runs the handler; the rest wait
        // and are served the freshly cached entry.
        var populateLockTimeout = pipelineSettings?.Value.PopulateLockTimeout
            ?? QueryCachePipelineSettings.DefaultPopulateLockTimeout;

        if (await TryAcquirePopulateLockAsync(cacheKey, populateLockTimeout, cancellationToken)
                .ConfigureAwait(false) is not { } stripe)
        {
            // Fail-open: stampede protection is an optimization, so a waiter that exhausts its
            // budget runs the query itself rather than failing. The result is deliberately NOT
            // cached here; the request holding the lock is the one that populates the entry.
            LogPopulateLockTimedOut(logger, cacheKey, queryName, populateLockTimeout.TotalSeconds);
            CqrsMetrics.RecordCacheMiss(queryName);
            return await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);
        }

        using (stripe)
        {
            cached = await TryReadAsync(cacheKey, queryName, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                CqrsMetrics.RecordCacheHit(queryName);
                return cached;
            }

            // One miss per executed query, recorded here rather than at either read: a request that
            // misses the fast path, takes the lock and misses the double-check has read the cache
            // twice but executed once, so counting at the reads would double-count it. This single
            // point is reached exactly when execution falls through to the inner handler. A read
            // that FAILED (cache outage, swallowed by TryReadAsync) also lands here and counts as a
            // miss, which is correct: the query went uncached either way.
            CqrsMetrics.RecordCacheMiss(queryName);

            var result = await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);

            // Only cache non-failure results
            if (result is not Shared.Abstractions.Result { IsFailure: true })
            {
                try
                {
                    await cacheService.SetAsync(cacheKey, result, cacheable.CacheDuration, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The handler already produced the answer, so a cache blip must not fail the
                    // query. The request token is kept, so real cancellation still propagates
                    // through the filter.
                    LogCachePopulateFailed(logger, cacheKey, queryName, ex);
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Cache read failed for key '{CacheKey}' on query '{QueryName}'; the read is treated as a miss and the query runs uncached")]
    private static partial void LogCacheReadFailed(
        ILogger logger,
        string cacheKey,
        string queryName,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Populate lock for key '{CacheKey}' on query '{QueryName}' was not acquired within {TimeoutSeconds}s; the query runs uncached")]
    private static partial void LogPopulateLockTimedOut(
        ILogger logger,
        string cacheKey,
        string queryName,
        double timeoutSeconds);

    /// <summary>
    /// Acquires the per-key populate lock, giving up once <paramref name="timeout"/> elapses.
    /// </summary>
    /// <remarks>
    /// The budget is armed on a LINKED source rather than the caller's token, so an expired budget
    /// is distinguishable from a genuinely cancelled request: the former returns
    /// <see langword="null"/> and the caller degrades to an uncached read, the latter still throws
    /// (the same split <see cref="TimeoutQueryDecorator{TQuery, TResult}"/> makes). A non-positive
    /// timeout means no bound and allocates nothing, which is the default path.
    /// </remarks>
    /// <param name="cacheKey">The tenant-scoped cache key the lock is striped on.</param>
    /// <param name="timeout">The wait budget; zero or less waits indefinitely.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The stripe handle, or <see langword="null"/> when the budget elapsed first.</returns>
    private static async Task<KeyedSemaphoreStripe.Releaser?> TryAcquirePopulateLockAsync(
        string cacheKey,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            return await QueryCacheKeyLocks.Locks.AcquireAsync(cacheKey, cancellationToken).ConfigureAwait(false);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        try
        {
            return await QueryCacheKeyLocks.Locks.AcquireAsync(cacheKey, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the cache fail-open: a cache fault is logged at warning level and reported as a miss
    /// so the caller falls through to the inner handler. Cancellation is deliberately not caught.
    /// </summary>
    /// <param name="cacheKey">The tenant-scoped cache key.</param>
    /// <param name="queryName">The query type name, used for logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached value, or the default when the key is absent or the cache is unreachable.</returns>
    private async Task<TResult?> TryReadAsync(
        string cacheKey,
        string queryName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await cacheService.GetAsync<TResult>(cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCacheReadFailed(logger, cacheKey, queryName, ex);
            return default;
        }
    }
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
/// (e.g. Redis), stampede protection is best-effort: at most one handler execution per
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
