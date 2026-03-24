namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Marker interface for commands that should trigger cache invalidation on success.
/// The <see cref="Decorators.CachingCommandDecorator{TCommand,TResult}"/> evicts all
/// cache entries matching <see cref="CachePrefix"/> after the command completes successfully.
/// </summary>
public interface ICacheInvalidating
{
    /// <summary>
    /// The cache key prefix to invalidate (e.g. "Catalog:Products").
    /// All cache entries with keys starting with this prefix will be removed.
    /// </summary>
    string CachePrefix { get; }
}
