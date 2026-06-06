using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

/// <summary>
/// Creates and caches <see cref="ApplicationDbContext"/> instances per physical
/// <see cref="DataSourceKey"/> (engine + database). Each physical source yields at most one
/// context per scope; subsequent calls return the cached instance.
/// Coordinates save, transaction, and disposal across all active contexts.
/// </summary>
public sealed class DbContextFactory(
    IPhysicalDbContextFactory physicalDbContextFactory,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    ICurrentUserService currentUserService
) : IDbContextFactory
{
    private readonly IPhysicalDbContextFactory _physicalDbContextFactory = physicalDbContextFactory ?? throw new ArgumentNullException(nameof(physicalDbContextFactory));
    private readonly IEntityDataSourceRegistry _entityDataSourceRegistry = entityDataSourceRegistry ?? throw new ArgumentNullException(nameof(entityDataSourceRegistry));
    private readonly IDataSourceResolver _dataSourceResolver = dataSourceResolver ?? throw new ArgumentNullException(nameof(dataSourceResolver));
    private readonly ICurrentUserService _currentUserService = currentUserService ?? throw new ArgumentNullException(nameof(currentUserService));

    /// <summary>
    /// Caches one context per physical data source so all repositories within a scope share the same change tracker.
    /// </summary>
    private readonly Dictionary<DataSourceKey, ApplicationDbContext> _dbContexts = [];

    /// <summary>
    /// Tracks whether a transaction is active so that contexts created lazily (after
    /// <see cref="BeginTransaction"/> was called) are automatically enlisted.
    /// </summary>
    private bool _transactionActive;

    /// <summary>
    /// When <see langword="true"/>, <see cref="SaveChangesAsync"/> scans the change tracker
    /// for Added entities with explicit identity values and handles
    /// <c>SET IDENTITY_INSERT ON/OFF</c> per table. Reset after each save.
    /// </summary>
    private bool _identityInsertRequested;

    private volatile bool _disposed;

    /// <inheritdoc />
    public ApplicationDbContext GetDbContext(DataSourceKey dataSourceKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(DbContextFactory));

        if (!_dbContexts.TryGetValue(dataSourceKey, out var context) || context is null)
        {
            context = _physicalDbContextFactory.Create(dataSourceKey);
            _dbContexts[dataSourceKey] = context;

            // Enlist late-created contexts in the active transaction so that all
            // persistence within a transactional command shares the same boundary.
            if (_transactionActive && SupportsTransactions(context))
                context.Database.BeginTransaction();
        }
        return context;
    }

    /// <inheritdoc />
    public ApplicationDbContext GetDbContext(DataSource dataSource) =>
        GetDbContext(DataSourceKey.Default(dataSource));

    /// <inheritdoc />
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in GetSourcesInUse())
        {
            // Sources without a configured connection string are skipped (e.g. Cosmos is
            // optional in integration tests that omit its connection string).
            if (string.IsNullOrEmpty(_dataSourceResolver.GetPhysical(key).ConnectionString))
                continue;

            await GetDbContext(key).Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The physical sources this host actually uses: every source backing a registered entity,
    /// plus any already-materialized contexts (e.g. the outbox publish target).
    /// </summary>
    private List<DataSourceKey> GetSourcesInUse() =>
        [.. _entityDataSourceRegistry.GetPhysicalSourcesInUse().Union(_dbContexts.Keys)];

    /// <inheritdoc />
    /// <remarks>
    /// Iterates all cached contexts and saves each with the current user's ID for audit stamping.
    /// When <see cref="RequestIdentityInsert"/> has been called, scans the change tracker for
    /// entities with explicit identity values and splits the save into multiple rounds with
    /// <c>SET IDENTITY_INSERT ON/OFF</c> per table.
    /// </remarks>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table and schema names are derived from EF model metadata, not user input.")]
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var identityInsertRequested = _identityInsertRequested;
        _identityInsertRequested = false;

        var result = 0;
        foreach (var context in _dbContexts.Values)
        {
            if (!identityInsertRequested || context is not SQLServerDbContext)
            {
                result += await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            result += await SaveWithIdentityInsertAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public void RequestIdentityInsert() => _identityInsertRequested = true;

    /// <summary>
    /// Saves changes for a SQL Server context that may contain entities with explicit
    /// identity values. Groups such entities by table and saves each group separately
    /// with <c>SET IDENTITY_INSERT ON/OFF</c>, respecting SQL Server's constraint that
    /// only one table may have <c>IDENTITY_INSERT ON</c> at a time per session.
    /// </summary>
    private async Task<int> SaveWithIdentityInsertAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var identityInsertGroups = GetIdentityInsertGroups(context);

        if (identityInsertGroups.Count == 0)
            return await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);

        int result = 0;
        var allIdentityEntries = identityInsertGroups.SelectMany(g => g.Entries).ToHashSet();

        foreach (var group in identityInsertGroups)
        {
            // Temporarily hide entries from OTHER identity-insert tables so they
            // are not included in this round's batch (avoids the one-table-at-a-time constraint).
            var savedStates = allIdentityEntries.Except(group.Entries)
                .Where(e => e.State == EntityState.Added)
                .Select(e => (Entry: e, OriginalState: e.State))
                .ToList();

            foreach (var (entry, _) in savedStates)
                entry.State = EntityState.Unchanged;

            await context.Database.ExecuteSqlRawAsync(
                string.Concat("SET IDENTITY_INSERT [", group.Schema, "].[", group.Table, "] ON"),
                cancellationToken).ConfigureAwait(false);

            result += await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);

            await context.Database.ExecuteSqlRawAsync(
                string.Concat("SET IDENTITY_INSERT [", group.Schema, "].[", group.Table, "] OFF"),
                cancellationToken).ConfigureAwait(false);

            foreach (var (entry, originalState) in savedStates)
                entry.State = originalState;
        }

        // Final save for any remaining changes (non-identity entities, updates, etc.)
        if (context.ChangeTracker.HasChanges())
        {
            result += await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Scans the change tracker for Added entities with identity columns that have
    /// explicit (non-default) values, grouped by their target table.
    /// </summary>
    private static List<IdentityInsertGroup> GetIdentityInsertGroups(ApplicationDbContext context)
    {
        var groups = new Dictionary<(string Schema, string Table), List<EntityEntry>>(2);

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added)
                continue;

            var entityType = entry.Metadata;
            var pk = entityType.FindPrimaryKey();
            if (pk is null || pk.Properties.Count != 1)
                continue;

            var idProp = pk.Properties[0];
            if (SqlServerPropertyExtensions.GetValueGenerationStrategy(idProp)
                != SqlServerValueGenerationStrategy.IdentityColumn)
            {
                continue;
            }

            // EF Core assigns temporary negative values to identity columns for entities
            // with default (0) IDs. Only entities with explicitly set (non-temporary) values
            // need IDENTITY_INSERT — those are the ones imported from an external system.
            if (entry.Property(idProp.Name).IsTemporary)
                continue;

            var schema = entityType.GetSchema() ?? "dbo";
            var table = entityType.GetTableName()!;
            var key = (schema, table);

            if (!groups.TryGetValue(key, out var entries))
            {
                entries = [];
                groups[key] = entries;
            }

            entries.Add(entry);
        }

        return [.. groups.Select(g => new IdentityInsertGroup(g.Key.Schema, g.Key.Table, g.Value))];
    }

    private sealed record IdentityInsertGroup(string Schema, string Table, List<EntityEntry> Entries);

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
    /// <remarks>
    /// With multiple physical sources, each gets its own transaction; commits are sequential and
    /// best-effort (no two-phase commit). The outbox is the cross-source consistency mechanism.
    /// </remarks>
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        // Use the execution strategy from the first active transactional context
        // (typically SQL Server). If none exists yet, create the default context so
        // the strategy is available before the handler's first repository call.
        var context = _dbContexts.Values.FirstOrDefault(SupportsTransactions)
            ?? GetDbContext(DataSource.SQLServer);

        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            BeginTransaction();
            try
            {
                var result = await operation(ct).ConfigureAwait(false);
                CommitTransaction();
                return result;
            }
            catch (OperationCanceledException)
            {
                // The connection may already be closed; best-effort rollback.
                // Disposal will clean up if this fails.
                try
                {
                    RollbackTransaction();
                }
                catch
                {
                    _transactionActive = false;
                }

                throw;
            }
            catch
            {
                RollbackTransaction();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in GetSourcesInUse().Where(k => k.Engine == DataSource.SQLServer))
            await GetDbContext(key).Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in GetSourcesInUse().Where(k => k.Engine == DataSource.SQLServer))
        {
            var pending = await GetDbContext(key).Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            foreach (var context in _dbContexts.Values)
                await context.DisposeAsync().ConfigureAwait(false);
            _dbContexts.Clear();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
