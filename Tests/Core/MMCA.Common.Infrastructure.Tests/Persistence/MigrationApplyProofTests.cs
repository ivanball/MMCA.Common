using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using MMCA.Common.Infrastructure.Tests.MigrationsFixture;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// End-to-end proof that the framework applies a REAL migration: a committed
/// <see cref="CreateMigrationProofTable"/> in a separate fixture assembly is run against a real
/// SQLite file through <see cref="DbContextFactory"/>, the same call
/// <c>DatabaseInitializationExtensions</c> makes for the <c>"Migrate"</c> strategy.
/// </summary>
/// <remarks>
/// Its sibling <c>DbContextFactoryMigrationTargetTests</c> proves which SOURCES the factory selects,
/// using a migrations assembly that declares nothing so that only the history table can appear.
/// This class proves the other half, that a declared migration actually reaches the schema, which is
/// why the migration lives in its own assembly: the two tests would otherwise contradict each other.
/// The pending-migration assertions below are the primitive the <c>"None"</c> strategy guard is
/// built on; the guard itself is covered where it lives, in
/// <c>MMCA.Common.API.Tests.Startup.DatabaseInitializationExtensionsTests</c>.
/// </remarks>
public sealed class MigrationApplyProofTests : IDisposable
{
    private const string SourceName = "MigrationProof";

    private static readonly string MigrationsAssemblyName =
        typeof(CreateMigrationProofTable).Assembly.GetName().Name!;

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"mmca-migration-proof-{Guid.NewGuid():N}.db");

    private readonly ServiceProvider _serviceProvider;
    private readonly DataSourceResolver _resolver;
    private readonly DbContextFactory _sut;
    private readonly DataSourceKey _key;

    public MigrationApplyProofTests()
    {
        var connectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=unused;" };
        var dataSources = new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            [SourceName] = new()
            {
                SqliteConnectionString = $"Data Source={_databasePath}",
                SqliteMigrationsAssembly = MigrationsAssemblyName,
            },
        });

        _resolver = new DataSourceResolver(Options.Create(connectionStrings), dataSources, NullLogger<DataSourceResolver>.Instance);
        _key = _resolver.ResolveLogical(DataSource.Sqlite, SourceName);

        var registry = new FixedSourcesRegistry([_key]);
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
        _sut = new DbContextFactory(physicalFactory, registry, _resolver, Mock.Of<ICurrentUserService>(), Mock.Of<ITenantContext>(), Options.Create(new TenancySettings()));
    }

    public void Dispose()
    {
        _sut.Dispose();
        _serviceProvider.Dispose();
        SqliteConnection.ClearAllPools();
        TryDelete(_databasePath);
    }

    [Fact]
    public async Task MigrateAsync_AppliesTheCommittedMigration()
    {
        await _sut.MigrateAsync();

        (await AppliedMigrationsAsync()).Should().ContainSingle()
            .Which.Should().Be(CreateMigrationProofTable.MigrationId,
                "the applied migration is recorded in the history table by its id");
        (await TableExistsAsync(CreateMigrationProofTable.TableName)).Should().BeTrue(
            "the migration's CreateTable reached the schema, not just the history table");
        (await _sut.HasPendingMigrationsAsync()).Should().BeFalse(
            "nothing is left pending once the only migration is applied");
    }

    // The other side of the same fact, and the one a production host depends on: before anything is
    // applied, the migration is reported as pending BY NAME. The "None" strategy turns exactly this
    // into a startup failure, so a host is never allowed to serve traffic against a schema that is
    // behind its code.
    [Fact]
    public async Task HasPendingMigrationsAsync_NamesTheMigrationBeforeItIsApplied()
    {
        (await _sut.HasPendingMigrationsAsync()).Should().BeTrue();

        var pending = await _sut.GetDbContext(_key).Database.GetPendingMigrationsAsync();
        pending.Should().ContainSingle().Which.Should().Be(CreateMigrationProofTable.MigrationId);

        (await TableExistsAsync(CreateMigrationProofTable.TableName)).Should().BeFalse(
            "asking what is pending must not apply anything");
    }

    // Startup runs on every boot and every replica, so a second Migrate over an up-to-date database
    // has to be a no-op rather than a re-apply (whose CREATE TABLE would hit the existing table).
    [Fact]
    public async Task MigrateAsync_RunTwice_IsANoOp()
    {
        await _sut.MigrateAsync();
        await _sut.MigrateAsync();

        (await AppliedMigrationsAsync()).Should().ContainSingle(
            "a migration already recorded in the history table is not applied again");
        (await TableExistsAsync(CreateMigrationProofTable.TableName)).Should().BeTrue();
    }

    /// <summary>The migration ids recorded in the database's <c>__EFMigrationsHistory</c> table.</summary>
    private async Task<IReadOnlyList<string>> AppliedMigrationsAsync()
    {
        if (!await TableExistsAsync("__EFMigrationsHistory"))
        {
            return [];
        }

        var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";

        var applied = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    /// <summary>Whether a table of the given name exists in the database under test.</summary>
    private async Task<bool> TableExistsAsync(string tableName)
    {
        var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private async Task<System.Data.Common.DbConnection> OpenConnectionAsync()
    {
        var connection = _sut.GetDbContext(_key).Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return connection;
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
    /// Reports the single source under test, so the topology is stated here rather than inferred
    /// from whichever entity configurations happen to live in this assembly.
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
    /// Supplies no configuration assemblies: the schema under test comes from the migration, which
    /// is the whole point, so the context's own model must contribute nothing.
    /// </summary>
    private sealed class NoConfigurationAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
