namespace MMCA.Common.Application.UseCases.Markers;

/// <summary>
/// Builds the framework's entity cache prefix, <c>"{type.FullName}:"</c>: the key space an
/// aggregate's cached reads live under and the prefix <see cref="ICacheInvalidating{TEntity}"/> and
/// the generic CRUD commands evict on success. It ends at the <c>:</c> boundary
/// <see cref="Interfaces.ICacheService.RemoveByPrefixAsync"/> requires.
/// </summary>
public static class EntityCachePrefix
{
    /// <summary>Gets the cache prefix for <typeparamref name="TEntity"/>.</summary>
    /// <typeparam name="TEntity">The aggregate type.</typeparam>
    /// <returns><c>"{typeof(TEntity).FullName}:"</c>.</returns>
    public static string For<TEntity>() => For(typeof(TEntity));

    /// <summary>Gets the cache prefix for <paramref name="entityType"/>.</summary>
    /// <param name="entityType">The aggregate type.</param>
    /// <returns><c>"{entityType.FullName}:"</c>.</returns>
    public static string For(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        return entityType.FullName + ":";
    }
}
