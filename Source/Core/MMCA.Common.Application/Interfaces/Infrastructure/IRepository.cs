using System.Linq.Expressions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Focused interface for single-entity lookups by ID.
/// Prefer this over <see cref="IReadRepository{TEntity,TIdentifierType}"/> when a handler
/// only needs <c>GetByIdAsync</c> or <c>ExistsAsync</c> — this signals minimal data access.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IEntityReader<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Retrieves a single entity by its primary key.</summary>
    Task<TEntity?> GetByIdAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves a single entity by its primary key with navigation properties eagerly loaded.</summary>
    Task<TEntity?> GetByIdAsync(
        TIdentifierType id,
        IEnumerable<string> includes,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves multiple entities by their primary keys in a single query.</summary>
    /// <param name="ids">The collection of primary keys to look up.</param>
    /// <param name="includes">Navigation properties to eager-load.</param>
    /// <param name="asTracking">Whether to track the returned entities for changes.</param>
    /// <param name="ignoreQueryFilters">Whether to bypass EF global query filters (e.g., soft-delete).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read-only collection of matching entities (may be fewer than requested if some IDs don't exist).</returns>
    Task<IReadOnlyCollection<TEntity>> GetByIdsAsync(
        IEnumerable<TIdentifierType> ids,
        IEnumerable<string>? includes = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether an entity with the given id exists.</summary>
    Task<bool> ExistsAsync(
        TIdentifierType id,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether any entity matches the predicate.</summary>
    Task<bool> ExistsAsync(
        Expression<Func<TEntity, bool>> where,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Focused interface for collection queries, projections, and counting.
/// Prefer this over <see cref="IReadRepository{TEntity,TIdentifierType}"/> when a handler
/// needs <c>GetAllAsync</c>, <c>GetProjectedAsync</c>, or <c>CountAsync</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IEntityQuerier<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Retrieves all entities matching optional includes, filter, ordering, and projection.</summary>
    Task<IReadOnlyCollection<TEntity>> GetAllAsync(
        IEnumerable<string> includes,
        Expression<Func<TEntity, bool>>? where = null,
        Expression<Func<TEntity, string>>? orderBy = null,
        Expression<Func<TEntity, TEntity>>? select = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves entities projected to a different type via a selector expression (translated to SQL).</summary>
    /// <typeparam name="TResult">The projected result type.</typeparam>
    Task<IReadOnlyCollection<TResult>> GetProjectedAsync<TResult>(
        Expression<Func<TEntity, TResult>> select,
        Expression<Func<TEntity, bool>>? where = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves entities as lightweight id/name pairs for lookup scenarios.</summary>
    Task<IReadOnlyCollection<BaseLookup<TIdentifierType>>> GetAllForLookupAsync(
        string nameProperty,
        Expression<Func<TEntity, bool>>? where = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the total count of entities.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the count of entities matching the predicate.</summary>
    Task<int> CountAsync(
        Expression<Func<TEntity, bool>> where,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only repository combining <see cref="IEntityReader{TEntity,TIdentifierType}"/>,
/// <see cref="IEntityQuerier{TEntity,TIdentifierType}"/>, and direct IQueryable access.
/// Existing code should continue using this interface; new handlers can depend on the
/// focused sub-interfaces for better ISP compliance.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IReadRepository<TEntity, TIdentifierType>
    : IEntityReader<TEntity, TIdentifierType>, IEntityQuerier<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
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

    /// <summary>Adds multiple entities to the persistence store in a single batch.</summary>
    /// <param name="entities">The entities to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task AddRangeAsync(
        IEnumerable<TEntity> entities,
        CancellationToken cancellationToken = default);

    /// <summary>Marks an existing entity as modified for persistence.</summary>
    /// <param name="entity">The entity to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateAsync(
        TEntity entity,
        CancellationToken cancellationToken = default);

    /// <summary>Marks multiple existing entities as modified for persistence in a single batch.</summary>
    /// <param name="entities">The entities to update.</param>
    void UpdateRange(IEnumerable<TEntity> entities);

    /// <summary>
    /// Executes a bulk delete directly in the database, bypassing change tracking.
    /// WARNING: Does NOT trigger domain events, audit stamps, or soft-delete behavior.
    /// Use only for maintenance scenarios where domain events are not needed.
    /// </summary>
    /// <param name="where">A predicate identifying the entities to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> ExecuteDeleteAsync(
        Expression<Func<TEntity, bool>> where,
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
