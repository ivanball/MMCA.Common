using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

/// <summary>
/// Creates and caches <see cref="ApplicationDbContext"/> instances per <see cref="DataSource"/>.
/// Each data source yields at most one context per scope; subsequent calls return the cached instance.
/// Coordinates save, transaction, and disposal across all active contexts.
/// </summary>
public sealed class DbContextFactory(
    IDbContextFactory<CosmosDbContext> dbContextFactoryCosmos,
    IDbContextFactory<SqliteDbContext> dbContextFactorySqlite,
    IDbContextFactory<SQLServerDbContext> dbContextFactorySQLServer,
    ICurrentUserService currentUserService
) : IDbContextFactory
{
    private readonly ICurrentUserService _currentUserService = currentUserService ?? throw new ArgumentNullException(nameof(currentUserService));

    /// <summary>
    /// Caches one context per DataSource so all repositories within a scope share the same change tracker.
    /// </summary>
    private readonly Dictionary<DataSource, ApplicationDbContext> _dbContexts = [];

    private readonly Dictionary<DataSource, Func<ApplicationDbContext>> _contextFactoryRegistry = new()
    {
        [DataSource.CosmosDB] = () => (dbContextFactoryCosmos ?? throw new ArgumentNullException(nameof(dbContextFactoryCosmos))).CreateDbContext(),
        [DataSource.Sqlite] = () => (dbContextFactorySqlite ?? throw new ArgumentNullException(nameof(dbContextFactorySqlite))).CreateDbContext(),
        [DataSource.SQLServer] = () => (dbContextFactorySQLServer ?? throw new ArgumentNullException(nameof(dbContextFactorySQLServer))).CreateDbContext(),
    };

    /// <summary>
    /// Tracks whether a transaction is active so that contexts created lazily (after
    /// <see cref="BeginTransaction"/> was called) are automatically enlisted.
    /// </summary>
    private bool _transactionActive;

    private volatile bool _disposed;

    /// <inheritdoc />
    public ApplicationDbContext GetDbContext(DataSource dataSource)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(DbContextFactory));

        if (!_dbContexts.TryGetValue(dataSource, out var context) || context is null)
        {
            context = CreateDbContext(dataSource);
            _dbContexts[dataSource] = context;

            // Enlist late-created contexts in the active transaction so that all
            // persistence within a transactional command shares the same boundary.
            if (_transactionActive && SupportsTransactions(context))
                context.Database.BeginTransaction();
        }
        return context;
    }

    private ApplicationDbContext CreateDbContext(DataSource dataSource) =>
        _contextFactoryRegistry.TryGetValue(dataSource, out var factory)
            ? factory()
            : throw new InvalidOperationException($"Invalid DataSource \"{dataSource}\"");

    /// <inheritdoc />
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var context in _dbContexts.Values)
            await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Iterates all cached contexts and saves each with the current user's ID for audit stamping.
    /// Returns the aggregate number of state entries written across all contexts.
    /// </remarks>
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var result = 0;
        foreach (var context in _dbContexts.Values)
            result += await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public int SaveChanges()
    {
        var result = 0;
        foreach (var context in _dbContexts.Values)
            result += context.SaveChanges();
        return result;
    }

    public void BeginTransaction()
    {
        _transactionActive = true;
        foreach (var context in _dbContexts.Values.Where(SupportsTransactions))
            context.Database.BeginTransaction();
    }

    public void CommitTransaction()
    {
        _transactionActive = false;
        foreach (var context in _dbContexts.Values.Where(SupportsTransactions).Where(HasActiveTransaction))
            context.Database.CommitTransaction();
    }

    public void RollbackTransaction()
    {
        _transactionActive = false;
        foreach (var context in _dbContexts.Values.Where(SupportsTransactions).Where(HasActiveTransaction))
            context.Database.RollbackTransaction();
    }

    /// <inheritdoc />
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        foreach (var context in _dbContexts.Values.Where(SupportsRelationalMigrations))
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var context in _dbContexts.Values.Where(SupportsRelationalMigrations))
        {
            var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            if (pending.Any())
                return true;
        }
        return false;
    }

    /// <summary>
    /// Cosmos DB does not support multi-document transactions via the EF provider;
    /// transaction operations are skipped for Cosmos contexts.
    /// </summary>
    private static bool SupportsTransactions(ApplicationDbContext context) =>
        context is not CosmosDbContext;

    private static bool HasActiveTransaction(ApplicationDbContext context) =>
        context.Database.CurrentTransaction is not null;

    /// <summary>
    /// Only relational contexts that have a migrations assembly configured support EF Core migrations.
    /// Cosmos and SQLite contexts are excluded.
    /// </summary>
    private static bool SupportsRelationalMigrations(ApplicationDbContext context) =>
        context is SQLServerDbContext;

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                foreach (var context in _dbContexts.Values)
                    context.Dispose();
                _dbContexts.Clear();
            }
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
