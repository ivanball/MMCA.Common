using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Persistence.DataSources;

/// <summary>
/// Fully resolved connection information for one physical data source (one database).
/// Produced by <see cref="IDataSourceResolver"/> from the top-level <c>ConnectionStrings</c>
/// section (the <c>Default</c> source) and the named <c>DataSources</c> entries.
/// </summary>
/// <param name="Key">The physical identity (engine + name) of this source.</param>
/// <param name="ConnectionString">The engine-specific connection string.</param>
/// <param name="MigrationsAssembly">
/// The EF Core migrations assembly for this source, taken from the configuration key of the
/// source's own engine (<c>SQLServerMigrationsAssembly</c>, <c>SqliteMigrationsAssembly</c> or
/// <c>PostgreSQLMigrationsAssembly</c>); <see langword="null"/> when none is configured. A physical
/// source belongs to exactly one engine, so one slot is enough, and the resolver never hands one
/// engine's assembly to another. Always <see langword="null"/> for Cosmos, which migrates nothing.
/// </param>
/// <param name="CosmosDatabaseName">The Cosmos DB database name. Ignored for relational engines.</param>
public sealed record PhysicalDataSource(
    DataSourceKey Key,
    string ConnectionString,
    string? MigrationsAssembly,
    string CosmosDatabaseName)
{
    /// <summary>
    /// Gets a value indicating whether this source participates in EF Core migrations, which is what
    /// decides between <c>Migrate</c> and <c>EnsureCreated</c> at startup for this one database.
    /// <para>
    /// SQL Server always does: a SQL Server host has been migration-driven since the first release,
    /// including the single-database monolith whose <c>Default</c> source names no migrations
    /// assembly at all and lets EF look next to the context. SQLite does only once a
    /// <see cref="MigrationsAssembly"/> is configured for it, because a SQLite source wired by hand
    /// before that setting existed has no migrations to apply and must keep being created outright.
    /// PostgreSQL follows the SQLite rule rather than the SQL Server one: it ships with no host that
    /// already depends on being migrated, so a source that names no migrations assembly has nothing
    /// to apply and is created outright instead of migrated into an empty schema.
    /// Cosmos never does: the provider has no migrations pipeline.
    /// </para>
    /// </summary>
    public bool UsesMigrations => Key.Engine switch
    {
        DataSource.SQLServer => true,
        DataSource.PostgreSQL or DataSource.Sqlite => !string.IsNullOrEmpty(MigrationsAssembly),
        DataSource.CosmosDB => false,
        _ => false,
    };
}
