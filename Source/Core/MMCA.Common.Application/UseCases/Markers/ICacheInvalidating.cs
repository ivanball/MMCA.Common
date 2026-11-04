namespace MMCA.Common.Application.UseCases.Markers;

/// <summary>
/// Marker interface for commands that should trigger cache invalidation on success.
/// The <see cref="Decorators.CachingCommandDecorator{TCommand,TResult}"/> evicts all
/// cache entries matching <see cref="CachePrefix"/> after the command completes successfully.
/// </summary>
public interface ICacheInvalidating
{
    /// <summary>
    /// The cache key prefix to invalidate, ending at a <c>:</c> boundary (e.g.
    /// <c>"Catalog:Products:"</c>, or the framework's default <c>"{typeof(TEntity).FullName}:"</c>).
    /// All cache entries with keys starting with this prefix will be removed.
    /// </summary>
    /// <remarks>
    /// The contract is <see cref="Interfaces.ICacheService.RemoveByPrefixAsync"/>'s: a prefix that
    /// does not end with <c>:</c> is rejected with an <see cref="ArgumentException"/>, which the
    /// decorator logs at Error level as a misconfigured command (the committed command still
    /// succeeds, and nothing is invalidated). An empty or whitespace value is the opt-out and
    /// evicts nothing.
    /// </remarks>
    string CachePrefix { get; }
}
