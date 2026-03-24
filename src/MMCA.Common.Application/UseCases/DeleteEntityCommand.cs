namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Generic delete command for any aggregate root entity. The <typeparamref name="TEntity"/>
/// type parameter is not used at runtime but is required so that DI can distinguish between
/// delete handlers for different entity types that share the same identifier type.
/// </summary>
/// <typeparam name="TEntity">The entity type to delete (used for DI type discrimination).</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <param name="Id">The primary key of the entity to delete.</param>
#pragma warning disable S2326 // TEntity is used for DI type discrimination between modules with same TIdentifierType
public sealed record DeleteEntityCommand<TEntity, TIdentifierType>(TIdentifierType Id)
    where TIdentifierType : notnull;
#pragma warning restore S2326
