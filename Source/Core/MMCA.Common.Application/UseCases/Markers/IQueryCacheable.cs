namespace MMCA.Common.Application.UseCases.Markers;

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
    /// <para>
    /// <b>The caller is such a parameter</b> whenever the result is that caller's own data, and it
    /// is the one that is not visible in the key. The decorator folds it in automatically for a
    /// query that also implements <c>IUserScopedRequest</c> (SEC-Common-37); a caller-scoped query
    /// that does NOT carry the target user on the request must either add that interface or put the
    /// caller into this key by hand, or the first caller's rows are served to the second for the
    /// whole <see cref="CacheDuration"/>. Opt back out with
    /// <see cref="ISharedQueryCache"/> when the result really is the same for everyone.
    /// </para>
    /// </summary>
    string CacheKey { get; }

    /// <summary>
    /// How long the cached result should be retained.
    /// </summary>
    TimeSpan CacheDuration { get; }
}
