using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Which sources <see cref="DbContextFactory.MigrateAsync"/> acts on, over two real SQLite
/// databases: one whose <c>DataSources</c> entry names a migrations assembly and one that names
/// none. The distinction is what lets a SQLite host apply a later migration at startup while a
/// SQLite source wired by hand before the setting existed keeps its EnsureCreated behaviour.
/// </summary>
public sealed class DbContextFactoryMigrationTargetTests : IDisposable
{
    private const string MigratedSourceName = "Migrated";
    private const string CreatedSourceName = "Created";

    private readonly string _migratedPath =
        Path.Combine(Path.GetTempPath(), $"mmca-migrated-{Guid.NewGuid():N}.db");

    private readonly string _createdPath =
        Path.Combine(Path.GetTempPath(), $"mmca-created-{Guid.NewGuid():N}.db");

    private readonly ServiceProvider _serviceProvider;
    private readonly DataSourceResolver _resolver;
    private readonly DbContextFactory _sut;
    private readonly DataSourceKey _migratedKey;
    private readonly DataSourceKey _createdKey;

    public DbContextFactoryMigrationTargetTests()
    {
        var connectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=unused;" };
        var dataSources = new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            [MigratedSourceName] = new()
            {
                SqliteConnectionString = $"Data Source={_migratedPath}",

                // The test assembly itself: it declares no migrations, which is exactly what makes
                // the assertion below unambiguous. Migrate creates the database and the history
                // table and nothing else, while EnsureCreated would have created the model's tables.
                SqliteMigrationsAssembly = typeof(DbContextFactoryMigrationTargetTests).Assembly.GetName().Name!,
            },
            [CreatedSourceName] = new() { SqliteConnectionString = $"Data Source={_createdPath}" },
        });

        _resolver = new DataSourceResolver(connectionStrings, dataSources, NullLogger<DataSourceResolver>.Instance);
        _migratedKey = _resolver.ResolveLogical(DataSource.Sqlite, MigratedSourceName);
        _createdKey = _resolver.ResolveLogical(DataSource.Sqlite, CreatedSourceName);

        var registry = new FixedSourcesRegistry([_migratedKey, _createdKey]);
        var assemblyProvider = new NoConfigurationAssemblyProvider();

        _serviceProvider = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton(Mock.Of<IDomainEventDispatcher>())
            .AddSingleton<IOutboxSignal, OutboxSignal>()
            .AddSingleton<AuditSaveChangesInterceptor>()
            .AddSingleton<DomainEventSaveChangesInterceptor>()
            .AddSingleton<IEntityDataSourceRegistry>(registry)
            .AddSingleton<IDataSourceResolver>(_resolver)
            .BuildServiceProvider();

        var physicalFactory = new PhysicalDbContextFactory(_serviceProvider, _resolver, assemblyProvider);
        _sut = new DbContextFactory(physicalFactory, registry, _resolver, Mock.Of<ICurrentUserService>());
    }

    public void Dispose()
    {
        _sut.Dispose();
        _serviceProvider.Dispose();
        SqliteConnection.ClearAllPools();
        TryDelete(_migratedPath);
        TryDelete(_createdPath);
    }

    [Fact]
    public async Task MigrateAsync_SqliteSourceWithAMigrationsAssembly_IsMigrated()
    {
        await _sut.MigrateAsync();

        File.Exists(_migratedPath).Should().BeTrue(
            "a SQLite source with a migrations assembly is a migration target");
        (await HistoryTableExistsAsync(_migratedKey)).Should().BeTrue(
            "Migrate records applied migrations, which is what a later 'migrations add' builds on");
    }

    // The other half of the same act: nothing may touch the source that has no migrations assembly,
    // because the API layer's EnsureCreated pass owns it. Migrating it would create an empty
    // database with a history table and no tables at all.
    [Fact]
    public async Task MigrateAsync_SqliteSourceWithoutAMigrationsAssembly_IsSkipped()
    {
        await _sut.MigrateAsync();

        File.Exists(_createdPath).Should().BeFalse(
            "a SQLite source with no migrations assembly keeps its EnsureCreated behaviour");
    }

    [Fact]
    public async Task HasPendingMigrationsAsync_ConsidersOnlyTheMigratedSource()
    {
        // No migrations exist in the migrations assembly, so nothing is pending. The value under
        // test is that the call completes at all: the source with no migrations assembly is never
        // asked, and asking it would surface as a provider failure rather than a false.
        (await _sut.HasPendingMigrationsAsync()).Should().BeFalse();

        File.Exists(_createdPath).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureCreatedAsync_StillCoversBothSources()
    {
        await _sut.EnsureCreatedAsync();

        File.Exists(_migratedPath).Should().BeTrue();
        File.Exists(_createdPath).Should().BeTrue(
            "the EnsureCreated strategy is unchanged: it creates every source in use");
    }

    /// <summary>
    /// Whether the database behind <paramref name="key"/> carries EF's migrations history table,
    /// which only <c>Migrate</c> creates.
    /// </summary>
    private async Task<bool> HistoryTableExistsAsync(DataSourceKey key)
    {
        var connection = _sut.GetDbContext(key).Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
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

    /// <summary>
    /// Reports a fixed set of sources in use, so the test states its topology directly instead of
    /// depending on which entity configurations happen to live in this assembly.
    /// </summary>
    private sealed class FixedSourcesRegistry(IReadOnlyCollection<DataSourceKey> sources) : IEntityDataSourceRegistry
    {
        public DataSourceKey GetDataSourceKey(Type entityType) =>
            throw new InvalidOperationException($"No data source registered for entity type \"{entityType}\".");

        public DataSourceKey GetDataSourceKey(string entityFullName) =>
            throw new InvalidOperationException($"No data source registered for entity \"{entityFullName}\".");

        public bool TryGetDataSourceKey(string entityFullName, out DataSourceKey key)
        {
            key = default;
            return false;
        }

        public IReadOnlyCollection<DataSourceKey> GetPhysicalSourcesInUse() => sources;
    }

    /// <summary>
    /// Supplies no configuration assemblies, keeping each context's model to what the framework
    /// itself declares. The question under test is source SELECTION, not schema.
    /// </summary>
    private sealed class NoConfigurationAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
