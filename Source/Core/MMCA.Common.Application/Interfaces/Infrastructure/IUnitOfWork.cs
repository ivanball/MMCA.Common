using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Coordinates persistence across multiple repositories within a single database context.
/// <see cref="SaveChangesAsync"/> persists all pending changes and dispatches domain events
/// raised by tracked aggregates.
/// </summary>
public interface IUnitOfWork : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets a read-write repository for an aggregate root entity.
    /// Only aggregate roots can be directly persisted.
    /// </summary>
    /// <typeparam name="TEntity">The aggregate root entity type.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
    /// <returns>A typed repository instance.</returns>
    IRepository<TEntity, TIdentifierType> GetRepository<TEntity, TIdentifierType>()
        where TEntity : AuditableAggregateRootEntity<TIdentifierType>
        where TIdentifierType : notnull;

    /// <summary>
    /// Gets a read-only repository for any entity (including non-aggregate entities).
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
    /// <returns>A typed read-only repository instance.</returns>
    IReadRepository<TEntity, TIdentifierType> GetReadRepository<TEntity, TIdentifierType>()
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull;

    /// <summary>Persists all pending changes and dispatches domain events.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of state entries written to the database.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Synchronous save. Prefer <see cref="SaveChangesAsync"/> in async code paths.</summary>
    /// <returns>The number of state entries written to the database.</returns>
    int Save();

    /// <summary>Begins a database transaction.</summary>
    void BeginTransaction();

    /// <summary>Commits the current transaction.</summary>
    void CommitTransaction();

    /// <summary>Rolls back the current transaction.</summary>
    void RollbackTransaction();
}
