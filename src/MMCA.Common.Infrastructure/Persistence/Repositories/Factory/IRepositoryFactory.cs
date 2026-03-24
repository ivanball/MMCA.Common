using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Infrastructure.Persistence.Repositories.Factory;

/// <summary>
/// Factory for creating repository instances. Conditionally wraps repositories in
/// MiniProfiler decorators when profiling is enabled.
/// </summary>
public interface IRepositoryFactory
{
    /// <summary>
    /// Creates a read-write repository for the given aggregate root entity and <see cref="DbContext"/>.
    /// </summary>
    /// <typeparam name="TEntity">The aggregate root entity type.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
    /// <param name="dbContext">The EF context the repository operates against.</param>
    IRepository<TEntity, TIdentifierType> Create<TEntity, TIdentifierType>(
        DbContext dbContext)
        where TEntity : AuditableAggregateRootEntity<TIdentifierType>
        where TIdentifierType : notnull;

    /// <summary>
    /// Creates a read-only repository for the given entity and <see cref="DbContext"/>.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
    /// <param name="dbContext">The EF context the repository operates against.</param>
    IReadRepository<TEntity, TIdentifierType> CreateReadOnly<TEntity, TIdentifierType>(
        DbContext dbContext)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull;
}
