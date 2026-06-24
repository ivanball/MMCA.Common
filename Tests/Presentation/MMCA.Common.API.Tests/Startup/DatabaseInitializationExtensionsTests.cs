using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
using MMCA.Common.Infrastructure.Settings;
using Moq;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// Tests for <see cref="DatabaseInitializationExtensions.InitializeDatabaseAsync"/>, focused on the
/// migration-less engines (SQLite, Cosmos). The SQL-Server-oriented <c>"Migrate"</c> strategy must
/// still create SQLite sources via <c>EnsureCreated</c> up front — without that, a SQLite source in
/// use is never created and the first repository call fails.
/// </summary>
public sealed class DatabaseInitializationExtensionsTests : IDisposable
{
    private readonly string _sqliteDbPath =
        Path.Combine(Path.GetTempPath(), $"mmca-init-sqlite-{Guid.NewGuid():N}.db");

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

        var resolver = new DataSourceResolver(connectionStrings, dataSources, NullLogger<DataSourceResolver>.Instance);
        var assemblyProvider = new FixedAssemblyProvider();
        var registry = new EntityDataSourceRegistry(assemblyProvider, resolver);

        await using var provider = new ServiceCollection()
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

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_sqliteDbPath);
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
}
