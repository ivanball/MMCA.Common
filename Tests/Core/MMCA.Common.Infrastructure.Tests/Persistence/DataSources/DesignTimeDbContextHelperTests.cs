using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Design;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

public sealed class DesignTimeDbContextHelperTests
{
    // ── --datasource argument parsing ──
    [Theory]
    [InlineData(new[] { "--datasource", "Conference" }, "Conference")]
    [InlineData(new[] { "--DataSource", "Conference" }, "Conference")]
    [InlineData(new[] { "--datasource=Conference" }, "Conference")]
    [InlineData(new[] { "--other", "x", "--datasource", "Identity" }, "Identity")]
    public void ParseDataSourceName_ValidArguments_ReturnsName(string[] args, string expected) =>
        DesignTimeDbContextHelper.ParseDataSourceName(args).Should().Be(expected);

    [Fact]
    public void ParseDataSourceName_NoArgument_ReturnsNull() =>
        DesignTimeDbContextHelper.ParseDataSourceName(["--other", "x"]).Should().BeNull();

    [Fact]
    public void ParseDataSourceName_MissingValue_Throws()
    {
        var act = () => DesignTimeDbContextHelper.ParseDataSourceName(["--datasource"]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*--datasource*");
    }

    // ── Context creation per data source ──
    [Fact]
    public void CreateSqlServer_NamedSource_BuildsModelWithOnlyThatSourcesEntities()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignAlpha"],
            ConfigureOptions);

        context.DataSourceKey.Should().Be(new DataSourceKey(DataSource.SQLServer, "DesignAlpha"));
        context.Model.FindEntityType(typeof(DesignAlphaEntity)).Should().NotBeNull();
        context.Model.FindEntityType(typeof(DesignBetaEntity)).Should().BeNull();
    }

    [Fact]
    public void CreateSqlServer_OtherNamedSource_BuildsItsOwnModel()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignBeta"],
            ConfigureOptions);

        context.DataSourceKey.Should().Be(new DataSourceKey(DataSource.SQLServer, "DesignBeta"));
        context.Model.FindEntityType(typeof(DesignBetaEntity)).Should().NotBeNull();
        context.Model.FindEntityType(typeof(DesignAlphaEntity)).Should().BeNull();
    }

    [Fact]
    public void CreateSqlServer_NoArgument_TargetsDefaultSource()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer([], ConfigureOptions);

        context.DataSourceKey.Should().Be(DataSourceKey.Default(DataSource.SQLServer));
    }

    [Fact]
    public void CreateSqlServer_ExplicitOptionName_OverridesArguments()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignBeta"],
            options =>
            {
                ConfigureOptions(options);
                options.DataSourceName = "DesignAlpha";
            });

        context.DataSourceKey.Name.Should().Be("DesignAlpha");
    }

    // ── Refresh-session table at design time ──
    // The scaffold is the only place the table can come from: `dotnet ef` never reads appsettings,
    // so without an explicit design-time flag the snapshot permanently lags the runtime model and
    // has-pending-model-changes reports drift forever.
    [Fact]
    public void CreateSqlServer_WithRefreshSessionsEnabled_IncludesTheSessionTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignSessions"],
            options =>
            {
                ConfigureOptions(options);
                options.EnableRefreshSessions = true;
            });

        context.DataSourceKey.Name.Should().Be("DesignSessions");
        context.Model.FindEntityType(typeof(RefreshSession)).Should().NotBeNull();
    }

    [Fact]
    public void CreateSqlServer_ByDefault_ExcludesTheSessionTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignNoSessions"],
            ConfigureOptions);

        context.Model.FindEntityType(typeof(RefreshSession)).Should().BeNull(
            "an existing migrations project must keep scaffolding exactly what it did before sessions shipped");
    }

    // The gate compares the context's PHYSICAL source name, and a logical name does not always
    // survive resolution: names sharing a connection collapse onto the alphabetically-first of them
    // (and a name matching the top-level connection collapses onto Default, which is the ADC shape).
    // Registering the requested logical name would miss the gate on every one of those hosts.
    [Fact]
    public void CreateSqlServer_WhenTheRequestedSourceCollapsesOntoAnother_StillIncludesTheSessionTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignSessionsZulu"],
            options =>
            {
                ConfigureOptions(options);
                options.EnableRefreshSessions = true;
            });

        context.DataSourceKey.Name.Should().Be(
            "DesignSessionsAlpha",
            "the two logical names share a connection, so they resolve to one physical source");
        context.Model.FindEntityType(typeof(RefreshSession)).Should().NotBeNull(
            "the gate must be opened for the source the context actually targets, not the one asked for");
    }

    // ── SQLite design time ──
    // The symmetric entry point a SQLite-backed application scaffolds through. Without it such an app
    // has no way to run `dotnet ef` against the framework's context at all.
    [Fact]
    public void CreateSqlite_NamedSource_BuildsTheSqliteContextForThatSource()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlite(
            ["--datasource", "DesignSqlite"],
            ConfigureSqliteOptions);

        context.DataSourceKey.Should().Be(new DataSourceKey(DataSource.Sqlite, "DesignSqlite"));
        context.Model.FindEntityType(typeof(DesignSqliteEntity)).Should().NotBeNull();
    }

    [Fact]
    public void CreateSqlite_MigrationsAssembly_ReachesTheEfOptions()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlite(
            ["--datasource", "DesignSqlite"],
            ConfigureSqliteOptions);

        RelationalOptionsExtension.Extract(context.GetService<IDbContextOptions>())
            .MigrationsAssembly.Should().Be(
                "Design.Sqlite.Migrations",
                "a setting that never reaches UseSqlite leaves EF looking for migrations next to the framework context");
    }

    [Fact]
    public void CreateSqlite_NoArgument_TargetsDefaultSource()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlite([], ConfigureSqliteOptions);

        context.DataSourceKey.Should().Be(DataSourceKey.Default(DataSource.Sqlite));
    }

    private static void ConfigureSqliteOptions(DesignTimeDbContextOptions options)
    {
        options.ConnectionStrings = new ConnectionStringSettings
        {
            SqliteConnectionString = "Data Source=design-main.db",
        };
        options.DataSources["DesignSqlite"] = new DataSourceEntrySettings
        {
            SqliteConnectionString = "Data Source=design-sqlite.db",
            SqliteMigrationsAssembly = "Design.Sqlite.Migrations",
        };
        options.AddConfigurationAssembly(typeof(DesignTimeDbContextHelperTests).Assembly);
    }

    private static void ConfigureOptions(DesignTimeDbContextOptions options)
    {
        options.ConnectionStrings = new ConnectionStringSettings
        {
            SQLServerConnectionString = "Server=design;Database=Main;",
        };
        options.DataSources["DesignAlpha"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=Alpha;",
            SQLServerMigrationsAssembly = "Design.Alpha.Migrations",
        };
        options.DataSources["DesignBeta"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=Beta;",
            SQLServerMigrationsAssembly = "Design.Beta.Migrations",
        };

        // Sources used only by the refresh-session cases. Each carries its own connection string, and
        // so its own physical source: EF caches a built model per (context type, source name) for the
        // life of the process, and reusing a name across two cases with different expectations would
        // decide both by whichever ran first.
        options.DataSources["DesignSessions"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=Sessions;",
            SQLServerMigrationsAssembly = "Design.Sessions.Migrations",
        };
        options.DataSources["DesignNoSessions"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=NoSessions;",
            SQLServerMigrationsAssembly = "Design.NoSessions.Migrations",
        };

        // A pair sharing one connection, which the resolver collapses onto the alphabetically-first
        // name: asking for Zulu yields a context whose physical source is Alpha.
        const string sharedConnection = "Server=design;Database=SharedSessions;";
        options.DataSources["DesignSessionsAlpha"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = sharedConnection,
            SQLServerMigrationsAssembly = "Design.SharedSessions.Migrations",
        };
        options.DataSources["DesignSessionsZulu"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = sharedConnection,
            SQLServerMigrationsAssembly = "Design.SharedSessions.Migrations",
        };

        options.AddConfigurationAssembly(typeof(DesignTimeDbContextHelperTests).Assembly);
    }

    // ── Test entities & configurations ──
    public sealed class DesignAlphaEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class DesignBetaEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>The SQLite-engine counterpart, mapped only into the SQLite design-time context.</summary>
    public sealed class DesignSqliteEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    [UseDatabase("DesignAlpha")]
    private sealed class DesignAlphaEntityConfiguration : EntityTypeConfigurationSQLServer<DesignAlphaEntity, int>;

    [UseDatabase("DesignBeta")]
    private sealed class DesignBetaEntityConfiguration : EntityTypeConfigurationSQLServer<DesignBetaEntity, int>;

    [UseDatabase("DesignSqlite")]
    private sealed class DesignSqliteEntityConfiguration : EntityTypeConfigurationSqlite<DesignSqliteEntity, int>;
}
