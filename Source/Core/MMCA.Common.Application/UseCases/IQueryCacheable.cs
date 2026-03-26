namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Marker interface for queries whose results should be cached. The
/// <see cref="Decorators.CachingQueryDecorator{TQuery,TResult}"/> checks the cache
/// before executing the handler and stores the result on a cache miss.
/// </summary>
public interface IQueryCacheable
{
    /// <summary>
    /// The cache key for this query instance. Should include all parameters that
    /// affect the query result (e.g. <c>"Catalog:Products:page=1&amp;size=10"</c>).
    /// </summary>
    string CacheKey { get; }

    /// <summary>
    /// How long the cached result should be retained.
    /// </summary>
    TimeSpan CacheDuration { get; }
}
