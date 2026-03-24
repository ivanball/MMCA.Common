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
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler.</typeparam>
public sealed class CachingCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ICacheService cacheService) : ICommandHandler<TCommand, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        var result = await inner.HandleAsync(command, cancellationToken);

        // Only invalidate cache on success — failed commands should not evict valid cache entries
        if (command is ICacheInvalidating cacheInvalidating && !IsFailure(result))
        {
            await cacheService.RemoveByPrefixAsync(cacheInvalidating.CachePrefix, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Checks whether the result is a <see cref="Shared.Abstractions.Result"/> in a failure state.
    /// Uses pattern matching because <typeparamref name="TResult"/> is not constrained to Result.
    /// </summary>
    private static bool IsFailure(TResult result) =>
        result is Shared.Abstractions.Result { IsFailure: true };
}
