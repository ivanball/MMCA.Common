using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// DbContext targeting SQL Server. Configured via connection string from <see cref="IConnectionStringSettings"/>.
/// </summary>
public sealed class SQLServerDbContext(
    DbContextOptions<SQLServerDbContext> options,
    IServiceProvider serviceProvider,
    IConnectionStringSettings connectionStringSettings,
    IEntityConfigurationAssemblyProvider assemblyProvider)
    : ApplicationDbContext(options, serviceProvider, assemblyProvider)
{
    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        optionsBuilder
            .UseSqlServer(
                connectionStringSettings.SQLServerConnectionString,
                sql =>
                {
                    if (!string.IsNullOrEmpty(connectionStringSettings.SQLServerMigrationsAssembly))
                    {
                        sql.MigrationsAssembly(connectionStringSettings.SQLServerMigrationsAssembly);
                    }

                    // Retry transient SQL Server failures (timeouts, transient network drops,
                    // throttling, deadlocks). Required so cold-replica startup connection attempts
                    // and ACA-side platform replica replacements don't surface as user-facing 5xx.
                    // NOTE: with retry-on-failure enabled, manual BeginTransactionAsync calls
                    // MUST be wrapped in Database.CreateExecutionStrategy().ExecuteAsync — the
                    // TransactionalCommandDecorator already does this.
                    sql.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorNumbersToAdd: null);
                })
            // Suppress PendingModelChangesWarning. Required by the microservices-extraction
            // architecture (the user's "shared SQLServerDbContext" decision): each extracted
            // service host registers only the entity configurations for its enabled modules,
            // so its runtime EF model is always a strict subset of the migration snapshot
            // (which captures the union of all modules' tables). EF Core 9+ promotes this
            // warning to an error inside Migrator.ValidateMigrations during MigrateAsync —
            // suppressing it lets each service start cleanly.
            //
            // The trade-off: monolith hosts no longer get the "you forgot to add a migration"
            // safety net. CI should run `dotnet ef migrations has-pending-model-changes`
            // against the migrations assembly with the FULL model loaded as a separate gate.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

        base.OnConfiguring(optionsBuilder);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ApplyConfigurationsForEntitiesInContext(DataSource.SQLServer, modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
}
