using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// DbContext targeting SQL Server. One instance exists per physical SQL Server data source
/// (database); the connection string and migrations assembly come from the resolved
/// <see cref="PhysicalDataSource"/>.
/// </summary>
public sealed class SQLServerDbContext(
    DbContextOptions<SQLServerDbContext> options,
    IServiceProvider serviceProvider,
    IEntityConfigurationAssemblyProvider assemblyProvider,
    PhysicalDataSource physicalDataSource)
    : ApplicationDbContext(options, serviceProvider, assemblyProvider, physicalDataSource)
{
    /// <summary>
    /// Resolved once per instance in a field initializer rather than read from the primary
    /// constructor parameter inside <see cref="OnConfiguring"/>: referencing the parameter from a
    /// member body would capture it into this type's state while the base constructor also receives
    /// it (CS9107). Caching per instance is safe and not pooling: these contexts are deliberately
    /// never pooled (see <c>PhysicalDbContextFactory</c>), so an instance is short lived and always
    /// reads the options current at its creation.
    /// <para>
    /// GetService, NOT GetRequiredService: the design-time provider behind <c>dotnet ef</c>
    /// registers no options at all and must not be made to throw. A missing registration and a null
    /// value both fall back to the defaults, which reproduce the previous implicit behavior.
    /// </para>
    /// </summary>
    private readonly PersistenceSettings _persistenceSettings =
        serviceProvider.GetService<IOptions<PersistenceSettings>>()?.Value ?? new PersistenceSettings();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        optionsBuilder
            .UseSqlServer(
                PhysicalSource.ConnectionString,
                sql =>
                {
                    if (!string.IsNullOrEmpty(PhysicalSource.SqlServerMigrationsAssembly))
                    {
                        sql.MigrationsAssembly(PhysicalSource.SqlServerMigrationsAssembly);
                    }

                    // Without this every command silently inherits ADO.NET's 30 second default with
                    // no way to tune it per environment.
                    sql.CommandTimeout(_persistenceSettings.CommandTimeoutSeconds);

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
