using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Infrastructure.Settings;
using MMCA.Common.Infrastructure.Tests.MigrationsFixture;
using Moq;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// Tests for <see cref="DatabaseInitializationExtensions.InitializeDatabaseAsync"/>, focused on the
/// migration-less engines (SQLite, Cosmos) and on the strategy contract. The SQL-Server-oriented
/// <c>"Migrate"</c> strategy must still create SQLite sources via <c>EnsureCreated</c> up front:
/// without that, a SQLite source in use is never created and the first repository call fails.
/// </summary>
public sealed class DatabaseInitializationExtensionsTests : IDisposable
{
    private readonly string _sqliteDbPath =
        Path.Combine(Path.GetTempPath(), $"mmca-init-sqlite-{Guid.NewGuid():N}.db");

    private readonly string _sqliteMigratedDbPath =
        Path.Combine(Path.GetTempPath(), $"mmca-init-sqlite-migrated-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task InitializeDatabaseAsync_MigrateStrategy_CreatesSqliteSource()
    {
        // Arrange: one SQLite source in use, "Migrate" strategy, and NO SQL Server entities — so
        // MigrateAsync (SQL-Server-only) is a no-op and only the new migration-less-engine loop
        // can create the SQLite schema.
        var connectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=unused;" };
        var dataSources = new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["TestSqlite"] = new() { SqliteConnectionString = $"Data Source={_sqliteDbPath}" },
        });

        var resolver = new DataSourceResolver(Options.Create(connectionStrings), dataSources, NullLogger<DataSourceResolver>.Instance);
        var assemblyProvider = new FixedAssemblyProvider();
        var registry = new EntityDataSourceRegistry(assemblyProvider, resolver);

        await using var provider = new ServiceCollection()
            .AddOptions()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton(Mock.Of<IDomainEventDispatcher>())
            .AddSingleton<IOutboxSignal, OutboxSignal>()
            .AddSingleton<AuditSaveChangesInterceptor>()
            .AddSingleton<DomainEventSaveChangesInterceptor>()
            .AddSingleton(Mock.Of<ICurrentUserService>())
            .AddSingleton<IEntityConfigurationAssemblyProvider>(assemblyProvider)
            .AddSingleton<IDataSourceResolver>(resolver)
            .AddSingleton<IEntityDataSourceRegistry>(registry)
            .AddSingleton<IPhysicalDbContextFactory, PhysicalDbContextFactory>()
            .AddScoped<ITenantContext, TenantContext>()
            .AddScoped<IDbContextFactory, DbContextFactory>()
            .BuildServiceProvider();

        var applicationSettings = new ApplicationSettings { DatabaseInitStrategy = "Migrate" };

        // Act
        await provider.InitializeDatabaseAsync(applicationSettings, new ModuleLoader());

        // Assert: the SQLite database file exists and the entity's table was created (a query
        // against a missing table would throw).
        File.Exists(_sqliteDbPath).Should().BeTrue("the Migrate strategy must EnsureCreate SQLite sources");

        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();
        var context = factory.GetDbContext(resolver.ResolveLogical(DataSource.Sqlite, "TestSqlite"));
        (await context.Set<InitTestWidget>().CountAsync()).Should().Be(0);
    }

    // A SQLite source that DOES name a migrations assembly is the small-app shape: its schema is
    // owned by a migrations project, so startup must migrate it and must NOT EnsureCreated it first
    // (EnsureCreated writes the tables with no __EFMigrationsHistory row, after which every
    // migration is both pending and un-appliable). Both sources are in use in this one host, so the
    // choice is made per source rather than per host.
    [Fact]
    public async Task InitializeDatabaseAsync_MigrateStrategy_MigratesTheSqliteSourceThatHasAMigrationsAssembly()
    {
        var connectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=unused;" };
        var dataSources = new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["TestSqlite"] = new() { SqliteConnectionString = $"Data Source={_sqliteDbPath}" },
            ["TestSqliteMigrated"] = new()
            {
                SqliteConnectionString = $"Data Source={_sqliteMigratedDbPath}",

                // The test assembly declares no migrations, so Migrate creates the database and the
                // history table and nothing else: the absence of the entity's table is what proves
                // EnsureCreated did not run against this source.
                SqliteMigrationsAssembly = typeof(DatabaseInitializationExtensionsTests).Assembly.GetName().Name!,
            },
        });

        var resolver = new DataSourceResolver(Options.Create(connectionStrings), dataSources, NullLogger<DataSourceResolver>.Instance);
        var assemblyProvider = new FixedAssemblyProvider();
        var registry = new EntityDataSourceRegistry(assemblyProvider, resolver);

        await using var provider = BuildProvider(resolver, registry, assemblyProvider);

        await provider.InitializeDatabaseAsync(
            new ApplicationSettings { DatabaseInitStrategy = "Migrate" },
            new ModuleLoader());

        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();

        var migrated = factory.GetDbContext(resolver.ResolveLogical(DataSource.Sqlite, "TestSqliteMigrated"));
        (await CountTablesAsync(migrated, "__EFMigrationsHistory")).Should().Be(1,
            "a SQLite source with a migrations assembly goes through Migrate");
        (await CountTablesAsync(migrated, nameof(InitTestMigratedWidget))).Should().Be(0,
            "EnsureCreated must not run first: it would leave the schema outside migration control");

        var created = factory.GetDbContext(resolver.ResolveLogical(DataSource.Sqlite, "TestSqlite"));
        (await CountTablesAsync(created, nameof(InitTestWidget))).Should().Be(1,
            "the source with no migrations assembly keeps its EnsureCreated behaviour");
        (await CountTablesAsync(created, "__EFMigrationsHistory")).Should().Be(0);
    }

    // The production guard, against a REAL committed migration rather than an empty migrations
    // assembly: "None" is what a deployed host runs, and its whole job is to refuse to start when
    // the schema is behind the code. The failure has to NAME the migration, because the operator
    // reading it needs to know what to apply. Nothing may be applied on the way out either: "None"
    // means none.
    [Fact]
    public async Task InitializeDatabaseAsync_NoneStrategy_FailsStartupNamingThePendingMigration()
    {
        var connectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=unused;" };
        var dataSources = new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["TestSqlite"] = new() { SqliteConnectionString = $"Data Source={_sqliteDbPath}" },
            ["TestSqliteMigrated"] = new()
            {
                SqliteConnectionString = $"Data Source={_sqliteMigratedDbPath}",
                SqliteMigrationsAssembly =
                    typeof(CreateMigrationProofTable).Assembly.GetName().Name!,
            },
        });

        var resolver = new DataSourceResolver(Options.Create(connectionStrings), dataSources, NullLogger<DataSourceResolver>.Instance);
        var assemblyProvider = new FixedAssemblyProvider();
        var registry = new EntityDataSourceRegistry(assemblyProvider, resolver);

        await using var provider = BuildProvider(resolver, registry, assemblyProvider);

        var act = () => provider.InitializeDatabaseAsync(
            new ApplicationSettings { DatabaseInitStrategy = "None" },
            new ModuleLoader());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{CreateMigrationProofTable.MigrationId}*");

        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();
        var context = factory.GetDbContext(resolver.ResolveLogical(DataSource.Sqlite, "TestSqliteMigrated"));
        (await CountTablesAsync(context, CreateMigrationProofTable.TableName)).Should().Be(0,
            "the 'None' strategy reports what is pending and applies nothing");
    }

    // The strategy names exactly two behaviours. A value outside that set is a configuration mistake
    // whose only other outcome is a schema nobody touched, discovered as a failing query in
    // production, so startup refuses it before a single database is opened.
    [Theory]
    [InlineData("EnsureCreated")]
    [InlineData("migrate")]
    [InlineData("")]
    public async Task InitializeDatabaseAsync_UnknownStrategy_FailsStartupNamingTheValidValues(string strategy)
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();

        var act = () => provider.InitializeDatabaseAsync(
            new ApplicationSettings { DatabaseInitStrategy = strategy },
            new ModuleLoader());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Valid values are: Migrate, None*");
    }

    /// <summary>Counts the tables of the given name in a SQLite context's database.</summary>
    private static async Task<int> CountTablesAsync(DbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ServiceProvider BuildProvider(
        DataSourceResolver resolver,
        EntityDataSourceRegistry registry,
        IEntityConfigurationAssemblyProvider assemblyProvider) =>
        new ServiceCollection()
            .AddOptions()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton(Mock.Of<IDomainEventDispatcher>())
            .AddSingleton<IOutboxSignal, OutboxSignal>()
            .AddSingleton<AuditSaveChangesInterceptor>()
            .AddSingleton<DomainEventSaveChangesInterceptor>()
            .AddSingleton(Mock.Of<ICurrentUserService>())
            .AddSingleton(assemblyProvider)
            .AddSingleton<IDataSourceResolver>(resolver)
            .AddSingleton<IEntityDataSourceRegistry>(registry)
            .AddSingleton<IPhysicalDbContextFactory, PhysicalDbContextFactory>()
            .AddScoped<ITenantContext, TenantContext>()
            .AddScoped<IDbContextFactory, DbContextFactory>()
            .BuildServiceProvider();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_sqliteDbPath);
        TryDelete(_sqliteMigratedDbPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort temp file cleanup.
        }
    }

    private sealed class FixedAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() =>
            [typeof(DatabaseInitializationExtensionsTests).Assembly];
    }

    public sealed class InitTestWidget : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    [UseDatabase("TestSqlite")]
    private sealed class InitTestWidgetConfiguration : EntityTypeConfigurationSqlite<InitTestWidget, int>;

    public sealed class InitTestMigratedWidget : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    [UseDatabase("TestSqliteMigrated")]
    private sealed class InitTestMigratedWidgetConfiguration
        : EntityTypeConfigurationSqlite<InitTestMigratedWidget, int>;
}
