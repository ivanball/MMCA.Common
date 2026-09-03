using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Scheduling;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

public sealed class DataSourceResolverTests
{
    private const string DefaultSql = "Server=localhost;Database=Main;";
    private const string OtherSql = "Server=localhost;Database=Other;";
    private const string DefaultSqlite = "Data Source=main.db";

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
        var sut = CreateSut(connectionStrings: new ConnectionStringSettings
        {
            SQLServerConnectionString = DefaultSql,
            SqliteConnectionString = DefaultSqlite,
        });

        sut.ResolveLogical(DataSource.Sqlite, name).Should().Be(DataSourceKey.Default(DataSource.Sqlite));
    }

    [Fact]
    public void ResolveLogical_EntryWithoutConnectionStringForEngine_CollapsesToDefault()
    {
        var sut = CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Conference"] = new() { SqliteConnectionString = "Data Source=conference.db" },
        });

        // The entry only configures Sqlite, so SQL Server resolution falls back to Default. The SQLite
        // side collapses too: the entry is the only SQLite database the host declares, and the
        // top-level section names none, so it IS this host's Default SQLite source.
        sut.ResolveLogical(DataSource.SQLServer, "Conference").Should().Be(DataSourceKey.Default(DataSource.SQLServer));
        sut.ResolveLogical(DataSource.Sqlite, "Conference").Should().Be(DataSourceKey.Default(DataSource.Sqlite));
        sut.GetPhysical(DataSourceKey.Default(DataSource.Sqlite)).ConnectionString
            .Should().Be("Data Source=conference.db");
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

    // ── SQLite migrations assembly ──
    [Fact]
    public void GetPhysical_SqliteSourceWithOwnMigrationsAssembly_UsesIt()
    {
        var sut = CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Tickets"] = new()
            {
                SqliteConnectionString = "Data Source=tickets.db",
                SqliteMigrationsAssembly = "Tickets.Migrations.Sqlite",
            },
        });

        var key = sut.ResolveLogical(DataSource.Sqlite, "Tickets");

        sut.GetPhysical(key).SqliteMigrationsAssembly.Should().Be("Tickets.Migrations.Sqlite");
    }

    // The two slots are per-engine on purpose: a mixed host declares both, and handing the SQL Server
    // assembly to UseSqlite would point EF at a snapshot describing a different database.
    [Fact]
    public void GetPhysical_SqliteSource_DoesNotInheritTheSqlServerMigrationsAssembly()
    {
        var sut = CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Tickets"] = new() { SqliteConnectionString = "Data Source=tickets.db" },
            },
            new ConnectionStringSettings
            {
                SQLServerConnectionString = DefaultSql,
                SQLServerMigrationsAssembly = "Main.Migrations",
                SqliteConnectionString = "Data Source=main.db",
            });

        var sqlite = sut.GetPhysical(sut.ResolveLogical(DataSource.Sqlite, "Tickets"));

        sqlite.SqliteMigrationsAssembly.Should().BeNull(
            "the entry declared none, and the top-level value belongs to SQL Server");
        sqlite.SqlServerMigrationsAssembly.Should().BeNull("this physical source is not a SQL Server one");
    }

    [Fact]
    public void GetPhysical_SqliteDefaultSource_TakesTheAssemblyFromACollapsedEntry()
    {
        // ConnectionStrings carries no SQLite migrations assembly, so a SQLite Default source
        // declares one through an entry whose connection matches the top-level value and therefore
        // collapses onto Default.
        var sut = CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Tickets"] = new()
                {
                    SqliteConnectionString = "Data Source=main.db",
                    SqliteMigrationsAssembly = "App.Migrations.Sqlite",
                },
            },
            new ConnectionStringSettings
            {
                SQLServerConnectionString = DefaultSql,
                SqliteConnectionString = "Data Source=main.db",
            });

        var key = sut.ResolveLogical(DataSource.Sqlite, "Tickets");

        key.Should().Be(DataSourceKey.Default(DataSource.Sqlite));
        sut.GetPhysical(key).SqliteMigrationsAssembly.Should().Be("App.Migrations.Sqlite");
    }

    [Fact]
    public void Constructor_SqliteSourcesSharingADatabase_WithConflictingMigrationsAssembly_Throws()
    {
        var act = () => CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Alpha"] = new() { SqliteConnectionString = "Data Source=shared.db", SqliteMigrationsAssembly = "Alpha.Sqlite" },
            ["Zebra"] = new() { SqliteConnectionString = "Data Source=shared.db", SqliteMigrationsAssembly = "Zebra.Sqlite" },
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*SqliteMigrationsAssembly*");
    }

    // ── Unconfigured-engine substitution ──
    // Every engine the framework picks for its OWN tables comes from a setting defaulting to SQL
    // Server (Outbox:DataSource, Scheduler:DataSource, AuditTrail:DataSource). A host that
    // configures only SQLite must serve them from SQLite rather than from a physical source whose
    // connection string is empty.
    [Fact]
    public void ResolveLogical_SqliteOnlyHost_ServesTheFrameworkSqlServerDefaultFromSqlite()
    {
        var sut = CreateSut(connectionStrings: new ConnectionStringSettings { SqliteConnectionString = DefaultSqlite });

        // The engine the outbox, scheduler and audit trail ask for out of the box.
        var scheduler = sut.ResolveLogical(new SchedulerSettings().DataSource, DataSourceKey.DefaultName);
        var outbox = sut.ResolveLogical(new OutboxSettings().DataSource, new OutboxSettings().DatabaseName);
        var auditTrail = sut.ResolveLogical(new AuditTrailSettings().DataSource, DataSourceKey.DefaultName);

        scheduler.Should().Be(DataSourceKey.Default(DataSource.Sqlite));
        outbox.Should().Be(DataSourceKey.Default(DataSource.Sqlite));
        auditTrail.Should().Be(DataSourceKey.Default(DataSource.Sqlite));
        sut.GetPhysical(scheduler).ConnectionString.Should().Be(DefaultSqlite);
    }

    [Fact]
    public void ResolveLogical_SqliteConfiguredOnANamedEntryOnly_StillSubstitutesSqlite()
    {
        // No top-level SQLite connection string: the only database in the host is a named entry,
        // which is the shape the small-app template generates.
        var sut = CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Notes"] = new() { SqliteConnectionString = "Data Source=notes.db" },
            },
            new ConnectionStringSettings());

        sut.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName)
            .Should().Be(DataSourceKey.Default(DataSource.Sqlite));
        sut.ResolveLogical(DataSource.SQLServer, "Notes")
            .Should().Be(DataSourceKey.Default(DataSource.Sqlite));

        // The point of the collapse: the framework's own tables resolve to Default, so Default has
        // to be the host's one database even though only the named entry declares it.
        sut.GetPhysical(DataSourceKey.Default(DataSource.Sqlite)).ConnectionString
            .Should().Be("Data Source=notes.db");
    }

    [Fact]
    public void Default_TakesTheMigrationsAssemblyOfTheSingleNamedEntryItCollapsesOnto()
    {
        var sut = CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Tickets"] = new()
                {
                    SQLServerConnectionString = DefaultSql,
                    SQLServerMigrationsAssembly = "Helpdesk.Migrations",
                },
            },
            new ConnectionStringSettings());

        var physical = sut.GetPhysical(sut.ResolveLogical(DataSource.SQLServer, "Tickets"));

        physical.Key.Should().Be(DataSourceKey.Default(DataSource.SQLServer));
        physical.ConnectionString.Should().Be(DefaultSql);
        physical.SqlServerMigrationsAssembly.Should().Be("Helpdesk.Migrations");
    }

    [Fact]
    public void Default_StaysEmpty_WhenSeveralNamedEntriesDeclareDifferentDatabasesAndNoTopLevelOneDoes()
    {
        var sut = CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Conference"] = new() { SQLServerConnectionString = DefaultSql },
                ["Engagement"] = new() { SQLServerConnectionString = OtherSql },
            },
            new ConnectionStringSettings());

        // Two databases and nothing naming which is shared: there is no single answer, so each keeps
        // its own physical source and Default names none of them.
        sut.ResolveLogical(DataSource.SQLServer, "Conference").Should().Be(new DataSourceKey(DataSource.SQLServer, "Conference"));
        sut.ResolveLogical(DataSource.SQLServer, "Engagement").Should().Be(new DataSourceKey(DataSource.SQLServer, "Engagement"));
        sut.GetPhysical(DataSourceKey.Default(DataSource.SQLServer)).ConnectionString.Should().BeEmpty();
    }

    [Fact]
    public void ResolveLogical_SqlServerOnlyHost_IsUnchanged()
    {
        var sut = CreateSut();

        sut.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName)
            .Should().Be(DataSourceKey.Default(DataSource.SQLServer));
        sut.ResolveLogical(DataSource.SQLServer, "Conference")
            .Should().Be(DataSourceKey.Default(DataSource.SQLServer));
    }

    [Fact]
    public void ResolveLogical_PolyglotHost_RoutesEachEngineToItself()
    {
        // ADR-018: both engines are configured, so nothing is substituted and each engine keeps its
        // own physical sources.
        var sut = CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Catalog"] = new() { SqliteConnectionString = "Data Source=catalog.db" },
                ["Sales"] = new() { SQLServerConnectionString = OtherSql },
            },
            new ConnectionStringSettings
            {
                SQLServerConnectionString = DefaultSql,
                SqliteConnectionString = DefaultSqlite,
            });

        sut.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName)
            .Should().Be(DataSourceKey.Default(DataSource.SQLServer));
        sut.ResolveLogical(DataSource.SQLServer, "Sales").Should().Be(new DataSourceKey(DataSource.SQLServer, "Sales"));
        sut.ResolveLogical(DataSource.Sqlite, "Catalog").Should().Be(new DataSourceKey(DataSource.Sqlite, "Catalog"));

        // A SQL Server logical name with no SQL Server connection collapses onto SQL Server's
        // Default, exactly as before: it is NOT redirected to the SQLite source of the same name.
        sut.ResolveLogical(DataSource.SQLServer, "Catalog").Should().Be(DataSourceKey.Default(DataSource.SQLServer));
    }

    [Fact]
    public void ResolveLogical_SqliteAndCosmosHost_PrefersTheRelationalEngine()
    {
        var sut = CreateSut(connectionStrings: new ConnectionStringSettings
        {
            SqliteConnectionString = DefaultSqlite,
            CosmosConnectionString = "AccountEndpoint=https://test;AccountKey=dGVzdA==",
        });

        // The framework's own tables are relational, so SQLite wins over Cosmos DB.
        sut.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName)
            .Should().Be(DataSourceKey.Default(DataSource.Sqlite));
        sut.ResolveLogical(DataSource.CosmosDB, DataSourceKey.DefaultName)
            .Should().Be(DataSourceKey.Default(DataSource.CosmosDB));
    }

    [Fact]
    public void ResolveLogical_CosmosOnlyHost_SubstitutesCosmos()
    {
        var sut = CreateSut(connectionStrings: new ConnectionStringSettings
        {
            CosmosConnectionString = "AccountEndpoint=https://test;AccountKey=dGVzdA==",
        });

        sut.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName)
            .Should().Be(DataSourceKey.Default(DataSource.CosmosDB));
    }

    [Fact]
    public void ResolveLogical_HostWithNoDatabaseAtAll_PassesTheEngineThrough()
    {
        var sut = CreateSut(connectionStrings: new ConnectionStringSettings());

        sut.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName)
            .Should().Be(DataSourceKey.Default(DataSource.SQLServer));
        sut.ResolveLogical(DataSource.Sqlite, DataSourceKey.DefaultName)
            .Should().Be(DataSourceKey.Default(DataSource.Sqlite));
    }

    private static DataSourceResolver CreateSut(
        Dictionary<string, DataSourceEntrySettings>? sources = null,
        ConnectionStringSettings? connectionStrings = null) =>
        new(
            Options.Create(connectionStrings ?? new ConnectionStringSettings { SQLServerConnectionString = DefaultSql }),
            new DataSourcesSettings(sources),
            NullLogger<DataSourceResolver>.Instance);
}
