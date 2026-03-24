using System.Linq.Expressions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Read-only repository for querying entities. Available for all entity types
/// (not restricted to aggregate roots).
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IReadRepository<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Retrieves all entities matching optional includes, filter, ordering, and projection.</summary>
    /// <param name="includes">Navigation property names to include.</param>
    /// <param name="where">Optional filter expression.</param>
    /// <param name="orderBy">Optional ordering expression.</param>
    /// <param name="select">Optional projection expression.</param>
    /// <param name="asTracking">Whether to track entities in the change tracker.</param>
    /// <param name="ignoreQueryFilters">Whether to bypass global query filters (e.g., soft-delete).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching entities.</returns>
    Task<IReadOnlyCollection<TEntity>> GetAllAsync(
        IEnumerable<string> includes,
        Expression<Func<TEntity, bool>>? where = null,
        Expression<Func<TEntity, string>>? orderBy = null,
        Expression<Func<TEntity, TEntity>>? select = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves entities as lightweight id/name pairs for lookup scenarios.</summary>
    /// <param name="nameProperty">The entity property to project as the display name.</param>
    /// <param name="where">Optional filter expression.</param>
    /// <param name="asTracking">Whether to track entities in the change tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of id/name lookup pairs.</returns>
    Task<IReadOnlyCollection<BaseLookup<TIdentifierType>>> GetAllForLookupAsync(
        string nameProperty,
        Expression<Func<TEntity, bool>>? where = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves a single entity by its primary key.</summary>
    /// <param name="id">The primary key value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entity, or <see langword="null"/> if not found.</returns>
    Task<TEntity?> GetByIdAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the total count of entities.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entity count.</returns>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the count of entities matching the predicate.</summary>
    /// <param name="where">The filter predicate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching entity count.</returns>
    Task<int> CountAsync(
        Expression<Func<TEntity, bool>> where,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether an entity with the given id exists.</summary>
    /// <param name="id">The primary key value.</param>
    /// <param name="ignoreQueryFilters">Whether to bypass global query filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if the entity exists.</returns>
    Task<bool> ExistsAsync(
        TIdentifierType id,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether any entity matches the predicate.</summary>
    /// <param name="where">The filter predicate.</param>
    /// <param name="ignoreQueryFilters">Whether to bypass global query filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if a matching entity exists.</returns>
    Task<bool> ExistsAsync(
        Expression<Func<TEntity, bool>> where,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);

    /// <summary>Base queryable with change tracking enabled.</summary>
    IQueryable<TEntity> Table { get; }

    /// <summary>Base queryable with no-tracking (read-only, best for queries).</summary>
    IQueryable<TEntity> TableNoTracking { get; }

    /// <summary>No-tracking queryable configured for single SQL query execution.</summary>
    IQueryable<TEntity> TableNoTrackingSingleQuery { get; }

    /// <summary>No-tracking queryable configured for split query execution (avoids cartesian explosion).</summary>
    IQueryable<TEntity> TableNoTrackingSplitQuery { get; }
}

/// <summary>
/// Write repository for persisting entity changes.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IWriteRepository<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Adds a new entity to the persistence store.</summary>
    /// <param name="entity">The entity to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task AddAsync(
        TEntity entity,
        CancellationToken cancellationToken = default);

    /// <summary>Marks an existing entity as modified for persistence.</summary>
    /// <param name="entity">The entity to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateAsync(
        TEntity entity,
        CancellationToken cancellationToken = default);

    /// <summary>Synchronous save. Prefer <see cref="SaveChangesAsync"/> in async code paths.</summary>
    /// <returns>The number of state entries written.</returns>
    int Save();

    /// <summary>Persists all pending changes asynchronously.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of state entries written.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Combined read-write repository interface.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IRepository<TEntity, TIdentifierType> : IReadRepository<TEntity, TIdentifierType>, IWriteRepository<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull;
