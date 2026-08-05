using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that invalidates cached data after a command succeeds, when the command
/// implements the <see cref="ICacheInvalidating"/> marker interface. Cache entries
/// matching the command's <see cref="ICacheInvalidating.CachePrefix"/> are evicted; an empty or
/// whitespace prefix is the opt-out and evicts nothing.
/// <para>
/// Invalidation is intentionally skipped on failure results to avoid evicting valid
/// cache entries when the mutation did not actually persist any changes.
/// </para>
/// <para>
/// Invalidation is best-effort and deliberately non-cancellable. The command has already
/// committed by the time it runs, so it uses <see cref="CancellationToken.None"/> rather than the
/// request token (a client disconnect must not abandon the cleanup and strand stale entries), and
/// any failure is logged at warning level and swallowed rather than propagated: a cache outage must
/// never turn a committed command into a failure. Entries left behind expire on their own TTL.
/// </para>
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler.</typeparam>
public sealed partial class CachingCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ICacheService cacheService,
    ILogger<CachingCommandDecorator<TCommand, TResult>> logger) : ICommandHandler<TCommand, TResult>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CachingCommandDecorator{TCommand, TResult}"/> class
    /// without a logger, discarding the invalidation-failure warnings.
    /// <para>
    /// Exists for source compatibility with consumers that construct the decorator directly (tests
    /// pinned to a released package version). DI never selects it: container resolution prefers the
    /// logger-bearing constructor, so production keeps logging.
    /// </para>
    /// </summary>
    /// <param name="inner">The wrapped command handler.</param>
    /// <param name="cacheService">The cache to invalidate after a successful command.</param>
    public CachingCommandDecorator(ICommandHandler<TCommand, TResult> inner, ICacheService cacheService)
        : this(inner, cacheService, NullLogger<CachingCommandDecorator<TCommand, TResult>>.Instance)
    {
    }

    /// <summary>
    /// Gets or sets the delay before the second, best-effort eviction. A query that missed the cache
    /// and started executing before this command committed can finish afterwards and re-populate the
    /// entry with pre-write state; the delayed re-eviction removes that repopulated entry. Settable so
    /// a test does not have to wait out the production delay.
    /// </summary>
    internal TimeSpan ReInvalidationDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the most recent delayed re-invalidation. Exposed so the fire-and-forget task is observed
    /// rather than dropped, and so a test can await it deterministically.
    /// </summary>
    internal Task InvalidationFollowUp { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        var result = await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        // Only invalidate cache on success — failed commands should not evict valid cache entries.
        // An empty prefix is the opt-out for commands that carry a defaulted prefix, and the guard
        // is load-bearing either way: RemoveByPrefixAsync("") would evict the entire cache.
        if (command is ICacheInvalidating cacheInvalidating
            && !string.IsNullOrWhiteSpace(cacheInvalidating.CachePrefix)
            && !IsFailure(result))
        {
            try
            {
                // CancellationToken.None, not the request token: the command has committed, so the
                // cleanup must outlive a caller that has already walked away.
                await cacheService.RemoveByPrefixAsync(cacheInvalidating.CachePrefix, CancellationToken.None)
                    .ConfigureAwait(false);

                // A read that missed the cache before this command committed can still be running its
                // handler against pre-write state and populate the entry after the eviction above.
                // A second, delayed eviction removes that repopulated entry. Best-effort like the
                // first one: ICacheService is a singleton, so the follow-up outlives the request scope
                // safely, and anything it cannot evict expires on its own TTL.
                InvalidationFollowUp = ReInvalidateAfterDelayAsync(cacheInvalidating.CachePrefix);
            }
#pragma warning disable CA1031 // Do not catch general exception types: invalidation is best-effort and must never fail a committed command
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCacheInvalidationFailed(logger, cacheInvalidating.CachePrefix, typeof(TCommand).Name, ex);
            }
        }

        return result;
    }

    /// <summary>
    /// Waits <see cref="ReInvalidationDelay"/> and evicts the prefix a second time, swallowing every
    /// failure exactly like the first eviction does.
    /// </summary>
    private async Task ReInvalidateAfterDelayAsync(string cachePrefix)
    {
        try
        {
            await Task.Delay(ReInvalidationDelay, CancellationToken.None).ConfigureAwait(false);
            await cacheService.RemoveByPrefixAsync(cachePrefix, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Do not catch general exception types: re-invalidation is best-effort and runs detached from the request
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCacheInvalidationFailed(logger, cachePrefix, typeof(TCommand).Name, ex);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Cache invalidation failed for prefix '{CachePrefix}' after command '{CommandName}' committed; stale entries expire on their own TTL")]
    private static partial void LogCacheInvalidationFailed(
        ILogger logger,
        string cachePrefix,
        string commandName,
        Exception exception);

    /// <summary>
    /// Checks whether the result is a <see cref="Shared.Abstractions.Result"/> in a failure state.
    /// Uses pattern matching because <typeparamref name="TResult"/> is not constrained to Result.
    /// </summary>
    private static bool IsFailure(TResult result) =>
        result is Shared.Abstractions.Result { IsFailure: true };
}
