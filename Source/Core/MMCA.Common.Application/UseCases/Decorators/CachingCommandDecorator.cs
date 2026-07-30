using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that invalidates cached data after a command succeeds, when the command
/// implements the <see cref="ICacheInvalidating"/> marker interface. Cache entries
/// matching the command's <see cref="ICacheInvalidating.CachePrefix"/> are evicted.
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
    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        var result = await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        // Only invalidate cache on success — failed commands should not evict valid cache entries
        if (command is ICacheInvalidating cacheInvalidating && !IsFailure(result))
        {
            try
            {
                // CancellationToken.None, not the request token: the command has committed, so the
                // cleanup must outlive a caller that has already walked away.
                await cacheService.RemoveByPrefixAsync(cacheInvalidating.CachePrefix, CancellationToken.None)
                    .ConfigureAwait(false);
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
