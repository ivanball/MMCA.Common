using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Repositories.Factory;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Coordinates persistence across multiple <see cref="IDbContextFactory"/> contexts.
/// Caches repository instances per entity type so all operations within a scope share
/// the same change tracker and context.
/// </summary>
internal sealed class UnitOfWork(IDbContextFactory dbContextFactory, IDataSourceService dataSourceService, IRepositoryFactory repositoryFactory) : IUnitOfWork
{
    private readonly IDbContextFactory _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    private readonly IRepositoryFactory _repositoryFactory = repositoryFactory ?? throw new ArgumentNullException(nameof(repositoryFactory));

    /// <summary>
    /// Repository instance cache keyed by the closed generic repository interface type
    /// (e.g., <c>IRepository&lt;Order, int&gt;</c>). Prevents creating duplicate repositories
    /// for the same entity within a single unit of work scope.
    /// </summary>
    private readonly Dictionary<Type, object> _repositories = [];

    private volatile bool _disposed;

    /// <inheritdoc />
    /// <remarks>
    /// Resolves the correct <see cref="DataSource"/> for the entity via <see cref="IDataSourceService"/>,
    /// obtains the matching DbContext, and creates a repository bound to it.
    /// The repository is cached so subsequent calls for the same entity type reuse the same instance.
    /// </remarks>
    public IRepository<TEntity, TIdentifierType> GetRepository<TEntity, TIdentifierType>()
        where TEntity : AuditableAggregateRootEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        var key = typeof(IRepository<TEntity, TIdentifierType>);
        if (!_repositories.TryGetValue(key, out var repository))
        {
            var dataSource = dataSourceService.GetDataSource(typeof(TEntity));
            var dbContext = _dbContextFactory.GetDbContext(dataSource);
            repository = _repositoryFactory.Create<TEntity, TIdentifierType>(dbContext);
            _repositories[key] = repository;
        }
        return (IRepository<TEntity, TIdentifierType>)repository;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Same resolution logic as <see cref="GetRepository{TEntity,TIdentifierType}"/> but returns
    /// a read-only repository without mutation support, suitable for query handlers.
    /// </remarks>
    public IReadRepository<TEntity, TIdentifierType> GetReadRepository<TEntity, TIdentifierType>()
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        var key = typeof(IReadRepository<TEntity, TIdentifierType>);
        if (!_repositories.TryGetValue(key, out var repository))
        {
            var dataSource = dataSourceService.GetDataSource(typeof(TEntity));
            var dbContext = _dbContextFactory.GetDbContext(dataSource);
            repository = _repositoryFactory.CreateReadOnly<TEntity, TIdentifierType>(dbContext);
            _repositories[key] = repository;
        }
        return (IReadRepository<TEntity, TIdentifierType>)repository;
    }

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContextFactory.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public int Save() => _dbContextFactory.SaveChanges();

    /// <inheritdoc />
    public void RequestIdentityInsert() => _dbContextFactory.RequestIdentityInsert();

    /// <inheritdoc />
    public void BeginTransaction() => _dbContextFactory.BeginTransaction();

    /// <inheritdoc />
    public void CommitTransaction() => _dbContextFactory.CommitTransaction();

    /// <inheritdoc />
    public void RollbackTransaction() => _dbContextFactory.RollbackTransaction();

    /// <inheritdoc />
    public Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default) =>
        _dbContextFactory.ExecuteInTransactionAsync(operation, cancellationToken);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await _dbContextFactory.DisposeAsync().ConfigureAwait(false);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _dbContextFactory.Dispose();
            }
            _disposed = true;
        }
    }
}
