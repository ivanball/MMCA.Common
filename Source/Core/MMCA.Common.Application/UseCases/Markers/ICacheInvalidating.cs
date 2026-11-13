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

/// <summary>
/// Entity-keyed form of <see cref="ICacheInvalidating"/>: a command names the aggregate whose cached
/// reads it makes stale, and the framework supplies the prefix
/// (<c>"{typeof(TEntity).FullName}:"</c>, see <see cref="EntityCachePrefix"/>), the same one the
/// generic CRUD commands evict by default. The
/// <see cref="Decorators.CachingCommandDecorator{TCommand,TResult}"/> still reads only the
/// non-generic <see cref="ICacheInvalidating.CachePrefix"/>, which this interface implements.
/// </summary>
/// <typeparam name="TEntity">The aggregate whose cached entries the command invalidates on success.</typeparam>
/// <remarks>
/// A command that declares a public <c>CachePrefix</c> of its own still compiles and its member wins
/// over this default, but the shared <c>CacheInvalidationConventionTestsBase</c> fitness rule flags it:
/// a deliberately custom prefix belongs on the non-generic marker and in that rule's allow-list.
/// </remarks>
public interface ICacheInvalidating<TEntity> : ICacheInvalidating
{
    /// <summary>Gets the entity prefix <see cref="EntityCachePrefix.For{TEntity}"/> builds for <typeparamref name="TEntity"/>.</summary>
    string ICacheInvalidating.CachePrefix => EntityCachePrefix.For<TEntity>();
}
