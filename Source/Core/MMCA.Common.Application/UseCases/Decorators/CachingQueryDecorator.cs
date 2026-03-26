using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that caches query results when the query implements <see cref="IQueryCacheable"/>.
/// On a cache hit the stored result is returned without executing the inner handler.
/// On a cache miss the handler is executed and the result is stored for subsequent requests.
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

        var cached = await cacheService.GetAsync<TResult>(cacheable.CacheKey, cancellationToken)
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
}
