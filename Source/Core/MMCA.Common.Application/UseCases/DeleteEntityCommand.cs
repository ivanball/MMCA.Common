namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Generic delete command for any aggregate root entity. The <typeparamref name="TEntity"/>
/// type parameter distinguishes delete handlers for different entity types that share the same
/// identifier type, and supplies the default cache prefix evicted after a successful delete.
/// </summary>
/// <typeparam name="TEntity">The entity type to delete.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <param name="Id">The primary key of the entity to delete.</param>
public sealed record DeleteEntityCommand<TEntity, TIdentifierType>(TIdentifierType Id) : ICacheInvalidating
    where TIdentifierType : notnull
{
    /// <summary>
    /// Gets the cache key prefix evicted after a successful delete. Defaults to the
    /// aggregate-prefix convention every consumer already keys its cached reads under
    /// (<c>{entity full name}:</c>), because the generic controller constructs this command
    /// itself and cannot supply one. Set it to an empty string to opt out of invalidation.
    /// </summary>
    public string CachePrefix { get; init; } = typeof(TEntity).FullName + ":";
}
