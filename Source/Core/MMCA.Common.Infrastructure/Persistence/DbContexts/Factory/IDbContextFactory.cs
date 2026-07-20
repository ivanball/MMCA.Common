using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

/// <summary>
/// Abstracts the creation and lifecycle management of <see cref="ApplicationDbContext"/> instances
/// across multiple data sources. Caches contexts per physical <see cref="DataSourceKey"/> within a
/// scope and coordinates saves, transactions, and disposal across all active contexts.
/// </summary>
public interface IDbContextFactory : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Returns the <see cref="ApplicationDbContext"/> for the specified physical data source,
    /// creating one if it doesn't exist in this scope.
    /// </summary>
    ApplicationDbContext GetDbContext(DataSourceKey dataSourceKey);

    /// <summary>
    /// Returns the <see cref="ApplicationDbContext"/> for the engine's <b>Default</b> physical
    /// source (the top-level connection strings). Convenience overload preserving the
    /// single-database call sites.
    /// </summary>
    ApplicationDbContext GetDbContext(DataSource dataSource);

    /// <summary>
    /// Ensures the databases of every physical data source in use by this host have been created
    /// (sources without a configured connection string are skipped).
    /// </summary>
    Task EnsureCreatedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves pending changes across all active contexts with audit stamping and domain event dispatch.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously saves pending changes across all active contexts.
    /// </summary>
    int SaveChanges();

    /// <summary>
    /// Signals that the next <see cref="SaveChangesAsync"/> may include entities with
    /// explicit values for database-generated identity columns. The save pipeline will
    /// handle <c>SET IDENTITY_INSERT ON/OFF</c> per table as needed. The flag is
    /// automatically cleared after the save completes.
    /// </summary>
    void RequestIdentityInsert();

    /// <summary>
    /// Begins a database transaction on all active contexts that support transactions.
    /// </summary>
    void BeginTransaction();

    /// <summary>
    /// Commits the active transaction on all active contexts that support transactions.
    /// </summary>
    void CommitTransaction();

    /// <summary>
    /// Rolls back the active transaction on all active contexts that support transactions.
    /// </summary>
    void RollbackTransaction();

    /// <summary>
    /// Executes <paramref name="operation"/> inside a database transaction, wrapped by the
    /// active execution strategy so that retrying strategies can retry the entire unit.
    /// <para>
    /// When the operation touches multiple physical data sources, each source gets its own
    /// transaction and the commits are sequential and best-effort — there is <b>no</b> distributed
    /// (two-phase) commit. A failure mid-commit leaves earlier sources committed. The outbox
    /// pattern is the cross-source consistency mechanism; design multi-source work accordingly.
    /// </para>
    /// </summary>
    /// <typeparam name="TResult">The type returned by the operation.</typeparam>
    /// <param name="operation">The work to execute inside the transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies pending EF Core migrations for every SQL Server physical data source in use by this
    /// host (each with its own migrations assembly). Cosmos and SQLite sources are skipped.
    /// </summary>
    Task MigrateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <see langword="true"/> if any SQL Server physical data source in use has pending
    /// migrations that have not been applied.
    /// </summary>
    Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken = default);
}
