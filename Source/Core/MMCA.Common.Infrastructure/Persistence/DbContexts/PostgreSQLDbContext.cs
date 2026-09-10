using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.Conversions;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// DbContext targeting PostgreSQL. One instance exists per physical PostgreSQL data source
/// (database); the connection string and migrations assembly come from the resolved
/// <see cref="PhysicalDataSource"/>.
/// <para>
/// The mapping conventions deliberately match <see cref="SQLServerDbContext"/> rather than
/// PostgreSQL house style: table and column names keep the framework's PascalCase identifiers and
/// the module schema, so an entity configuration body is portable between the two engines with no
/// edits (ADR-113). A consumer that wants <c>snake_case</c> adds a naming-convention plugin in its
/// own host; the framework does not impose one.
/// </para>
/// </summary>
public sealed class PostgreSQLDbContext(
    DbContextOptions<PostgreSQLDbContext> options,
    IServiceProvider serviceProvider,
    IEntityConfigurationAssemblyProvider assemblyProvider,
    PhysicalDataSource physicalDataSource)
    : ApplicationDbContext(options, serviceProvider, assemblyProvider, physicalDataSource)
{
    /// <summary>
    /// The PostgreSQL store type every <see cref="DateTime"/> is mapped to. Named here so the
    /// context and the tests assert the same string.
    /// </summary>
    internal const string TimestampWithTimeZone = "timestamp with time zone";

    /// <summary>
    /// Resolved once per instance in a field initializer for exactly the reason documented on
    /// <see cref="SQLServerDbContext"/>: reading the primary constructor parameter from a member
    /// body would capture it into this type's state while the base constructor also receives it
    /// (CS9107). GetService, not GetRequiredService, so the design-time provider behind
    /// <c>dotnet ef</c> (which registers no options at all) falls back to the defaults.
    /// </summary>
    private readonly PersistenceSettings _persistenceSettings =
        serviceProvider.GetService<IOptions<PersistenceSettings>>()?.Value ?? new PersistenceSettings();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        optionsBuilder
            .UseNpgsql(
                PhysicalSource.ConnectionString,
                npgsql =>
                {
                    // Same contract as SQLServerDbContext and SqliteDbContext: without an explicit
                    // assembly EF looks for migrations next to the context, which lives in
                    // MMCA.Common.Infrastructure and has none.
                    if (!string.IsNullOrEmpty(PhysicalSource.PostgreSQLMigrationsAssembly))
                    {
                        npgsql.MigrationsAssembly(PhysicalSource.PostgreSQLMigrationsAssembly);
                    }

                    // Without this every command silently inherits the ADO.NET default with no way
                    // to tune it per environment.
                    npgsql.CommandTimeout(_persistenceSettings.CommandTimeoutSeconds);

                    // Retry transient failures (connection drops, a managed instance failing over,
                    // a pooler recycling a backend) for the same reason the SQL Server context does:
                    // a cold-start connection attempt must not surface as a user-facing 5xx.
                    // NOTE: with retry-on-failure enabled, manual BeginTransactionAsync calls MUST
                    // be wrapped in Database.CreateExecutionStrategy().ExecuteAsync; the
                    // TransactionalCommandDecorator already does this.
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorCodesToAdd: null);
                })
            // Suppressed for the microservices-extraction reason documented on SQLServerDbContext:
            // each extracted service host registers only its enabled modules' configurations, so its
            // runtime model is a strict subset of the migration snapshot.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

        base.OnConfiguring(optionsBuilder);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Adds the UTC normalization every PostgreSQL timestamp needs on top of the cross-engine
    /// conventions the base registers. Declared as a type-mapping configuration rather than
    /// property-by-property so it reaches the framework's own tables (outbox, inbox, scheduled jobs,
    /// audit trail, refresh sessions) and a consumer's entities alike, including nullable
    /// <see cref="DateTime"/> properties, which EF configures from the same non-nullable entry.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        configurationBuilder
            .Properties<DateTime>()
            .HaveConversion<UtcDateTimeConverter>()
            .HaveColumnType(TimestampWithTimeZone);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ApplyConfigurationsForEntitiesInContext(DataSource.PostgreSQL, modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
}
