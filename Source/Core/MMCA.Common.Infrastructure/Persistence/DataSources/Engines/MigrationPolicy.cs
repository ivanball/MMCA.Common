namespace MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

/// <summary>
/// How an engine decides whether one physical source is migrated (<c>Migrate</c>) or created
/// outright (<c>EnsureCreated</c>) at startup; read by
/// <see cref="PhysicalDataSource.UsesMigrations"/>.
/// </summary>
internal enum MigrationPolicy
{
    /// <summary>The provider has no migrations pipeline (Cosmos).</summary>
    Never,

    /// <summary>Migrated only once a migrations assembly is configured for the source (SQLite, PostgreSQL).</summary>
    WhenAssemblyConfigured,

    /// <summary>
    /// Always migrated, even with no migrations assembly (EF then looks next to the context), and a
    /// named source with none falls back to the Default source's assembly with a warning (SQL Server).
    /// </summary>
    Always,
}
