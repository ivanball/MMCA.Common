using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

public sealed class DataSourceResolverTests
{
    private const string DefaultSql = "Server=localhost;Database=Main;";
    private const string OtherSql = "Server=localhost;Database=Other;";

    // ── Collapse rules ──
    [Fact]
    public void ResolveLogical_NameWithoutEntry_CollapsesToDefault()
    {
        var sut = CreateSut();

        var key = sut.ResolveLogical(DataSource.SQLServer, "Conference");

        key.Should().Be(DataSourceKey.Default(DataSource.SQLServer));
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void ResolveLogical_DefaultName_AnyCase_ReturnsDefault(string name)
    {
        var sut = CreateSut();

        sut.ResolveLogical(DataSource.Sqlite, name).Should().Be(DataSourceKey.Default(DataSource.Sqlite));
    }

    [Fact]
    public void ResolveLogical_EntryWithoutConnectionStringForEngine_CollapsesToDefault()
    {
        var sut = CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Conference"] = new() { SqliteConnectionString = "Data Source=conference.db" },
        });

        // The entry only configures Sqlite — SQL Server resolution falls back to Default.
        sut.ResolveLogical(DataSource.SQLServer, "Conference").Should().Be(DataSourceKey.Default(DataSource.SQLServer));
        sut.ResolveLogical(DataSource.Sqlite, "Conference").Should().Be(new DataSourceKey(DataSource.Sqlite, "Conference"));
    }

    [Fact]
    public void ResolveLogical_EntryEqualToTopLevelConnection_CollapsesToDefault()
    {
        var sut = CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Conference"] = new() { SQLServerConnectionString = DefaultSql },
        });

        sut.ResolveLogical(DataSource.SQLServer, "Conference").Should().Be(DataSourceKey.Default(DataSource.SQLServer));
    }

    [Fact]
    public void ResolveLogical_EntryWithDistinctConnection_YieldsNamedKey()
    {
        var sut = CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Conference"] = new() { SQLServerConnectionString = OtherSql },
        });

        sut.ResolveLogical(DataSource.SQLServer, "Conference").Should().Be(new DataSourceKey(DataSource.SQLServer, "Conference"));
    }

    [Fact]
    public void ResolveLogical_EntriesSharingConnection_CollapseToAlphabeticallyFirstName()
    {
        var sut = CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Zebra"] = new() { SQLServerConnectionString = OtherSql },
            ["Alpha"] = new() { SQLServerConnectionString = OtherSql },
        });

        var expected = new DataSourceKey(DataSource.SQLServer, "Alpha");
        sut.ResolveLogical(DataSource.SQLServer, "Zebra").Should().Be(expected);
        sut.ResolveLogical(DataSource.SQLServer, "Alpha").Should().Be(expected);
    }

    // ── Cosmos identity includes the database name ──
    [Fact]
    public void ResolveLogical_Cosmos_SameAccountDifferentDatabase_AreDistinctSources()
    {
        var sut = CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Conference"] = new() { CosmosConnectionString = "AccountEndpoint=https://acc;", CosmosDatabaseName = "ConfDb" },
                ["Identity"] = new() { CosmosConnectionString = "AccountEndpoint=https://acc;", CosmosDatabaseName = "IdDb" },
            },
            new ConnectionStringSettings { SQLServerConnectionString = DefaultSql, CosmosConnectionString = "AccountEndpoint=https://other;" });

        var conference = sut.ResolveLogical(DataSource.CosmosDB, "Conference");
        var identity = sut.ResolveLogical(DataSource.CosmosDB, "Identity");

        conference.Should().NotBe(identity);
        sut.GetPhysical(conference).CosmosDatabaseName.Should().Be("ConfDb");
        sut.GetPhysical(identity).CosmosDatabaseName.Should().Be("IdDb");
    }

    [Fact]
    public void ResolveLogical_Cosmos_SameAccountSameDatabase_Collapse()
    {
        var sut = CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Conference"] = new() { CosmosConnectionString = "AccountEndpoint=https://acc;", CosmosDatabaseName = "Shared" },
                ["Identity"] = new() { CosmosConnectionString = "AccountEndpoint=https://acc;", CosmosDatabaseName = "Shared" },
            },
            new ConnectionStringSettings { SQLServerConnectionString = DefaultSql, CosmosConnectionString = "AccountEndpoint=https://other;" });

        sut.ResolveLogical(DataSource.CosmosDB, "Conference")
            .Should().Be(sut.ResolveLogical(DataSource.CosmosDB, "Identity"));
    }

    // ── Reserved name ──
    [Theory]
    [InlineData("Default")]
    [InlineData("default")]
    public void DataSourcesSettings_ReservedEntryName_Throws(string name)
    {
        var act = () => new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            [name] = new(),
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*reserved*");
    }

    // ── Migrations assembly resolution ──
    [Fact]
    public void Constructor_EntryCollapsedToDefault_WithConflictingMigrationsAssembly_Throws()
    {
        var act = () => CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Conference"] = new() { SQLServerConnectionString = DefaultSql, SQLServerMigrationsAssembly = "Conference.Migrations" },
            },
            new ConnectionStringSettings { SQLServerConnectionString = DefaultSql, SQLServerMigrationsAssembly = "Main.Migrations" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*SQLServerMigrationsAssembly*");
    }

    [Fact]
    public void Constructor_EntriesSharingConnection_WithConflictingMigrationsAssemblies_Throws()
    {
        var act = () => CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Alpha"] = new() { SQLServerConnectionString = OtherSql, SQLServerMigrationsAssembly = "Alpha.Migrations" },
            ["Zebra"] = new() { SQLServerConnectionString = OtherSql, SQLServerMigrationsAssembly = "Zebra.Migrations" },
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*SQLServerMigrationsAssembly*");
    }

    [Fact]
    public void GetPhysical_EntryCollapsedToDefault_WithAgreedMigrationsAssembly_DoesNotThrow()
    {
        var sut = CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Conference"] = new() { SQLServerConnectionString = DefaultSql, SQLServerMigrationsAssembly = "Main.Migrations" },
            },
            new ConnectionStringSettings { SQLServerConnectionString = DefaultSql, SQLServerMigrationsAssembly = "Main.Migrations" });

        sut.GetPhysical(DataSourceKey.Default(DataSource.SQLServer))
            .SqlServerMigrationsAssembly.Should().Be("Main.Migrations");
    }

    [Fact]
    public void GetPhysical_NamedSourceWithOwnMigrationsAssembly_UsesIt()
    {
        var sut = CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Conference"] = new() { SQLServerConnectionString = OtherSql, SQLServerMigrationsAssembly = "Conference.Migrations" },
        });

        var key = sut.ResolveLogical(DataSource.SQLServer, "Conference");

        sut.GetPhysical(key).SqlServerMigrationsAssembly.Should().Be("Conference.Migrations");
    }

    [Fact]
    public void GetPhysical_NamedSourceWithoutMigrationsAssembly_FallsBackToTopLevel()
    {
        var sut = CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Conference"] = new() { SQLServerConnectionString = OtherSql },
            },
            new ConnectionStringSettings { SQLServerConnectionString = DefaultSql, SQLServerMigrationsAssembly = "Main.Migrations" });

        var key = sut.ResolveLogical(DataSource.SQLServer, "Conference");

        sut.GetPhysical(key).SqlServerMigrationsAssembly.Should().Be("Main.Migrations");
    }

    // ── GetPhysical ──
    [Fact]
    public void GetPhysical_Default_ReturnsTopLevelValues()
    {
        var sut = CreateSut();

        var physical = sut.GetPhysical(DataSourceKey.Default(DataSource.SQLServer));

        physical.ConnectionString.Should().Be(DefaultSql);
        physical.CosmosDatabaseName.Should().Be("AtlDevCon");
    }

    [Fact]
    public void GetPhysical_UnknownNamedKey_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.GetPhysical(new DataSourceKey(DataSource.SQLServer, "Unknown"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Unknown*");
    }

    [Fact]
    public void GetPhysical_NamedSource_ReturnsEntryConnection()
    {
        var sut = CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Conference"] = new() { SQLServerConnectionString = OtherSql },
        });

        var key = sut.ResolveLogical(DataSource.SQLServer, "Conference");

        sut.GetPhysical(key).ConnectionString.Should().Be(OtherSql);
    }

    private static DataSourceResolver CreateSut(
        Dictionary<string, DataSourceEntrySettings>? sources = null,
        ConnectionStringSettings? connectionStrings = null) =>
        new(
            connectionStrings ?? new ConnectionStringSettings { SQLServerConnectionString = DefaultSql },
            new DataSourcesSettings(sources),
            NullLogger<DataSourceResolver>.Instance);
}
