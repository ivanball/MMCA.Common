using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.FeatureFlags;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.Conversions;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Design;
using MMCA.Common.Shared.Identifiers;

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

    // Same two-part gate for the stored permission-grants table (ADR-116): the flag registers the
    // framework's marker, and the settings name the source THIS context resolved to.
    [Fact]
    public void CreateSqlServer_WithStoredPermissionGrantsEnabled_IncludesThePermissionGrantsTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignGrants"],
            options =>
            {
                ConfigureOptions(options);
                options.EnableStoredPermissionGrants = true;
            });

        context.DataSourceKey.Name.Should().Be("DesignGrants");
        context.Model.FindEntityType(typeof(PermissionGrant)).Should().NotBeNull();
    }

    [Fact]
    public void CreateSqlServer_ByDefault_ExcludesThePermissionGrantsTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignNoGrants"],
            ConfigureOptions);

        context.Model.FindEntityType(typeof(PermissionGrant)).Should().BeNull(
            "a migrations project that never opts in must keep scaffolding exactly what it did before");
    }

    [Fact]
    public void CreateSqlServer_WhenTheRequestedSourceCollapsesOntoAnother_StillIncludesThePermissionGrantsTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignGrantsZulu"],
            options =>
            {
                ConfigureOptions(options);
                options.EnableStoredPermissionGrants = true;
            });

        context.DataSourceKey.Name.Should().Be("DesignGrantsAlpha");
        context.Model.FindEntityType(typeof(PermissionGrant)).Should().NotBeNull(
            "the gate must be opened for the source the context actually targets, not the one asked for");
    }

    // Same two-part gate for the stored feature-flag overrides (ADR-031).
    [Fact]
    public void CreateSqlServer_WithFeatureFlagOverridesEnabled_IncludesTheOverrideTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignFlags"],
            options =>
            {
                ConfigureOptions(options);
                options.EnableFeatureFlagOverrides = true;
            });

        context.Model.FindEntityType(typeof(FeatureFlagOverride)).Should().NotBeNull();
    }

    [Fact]
    public void CreateSqlServer_ByDefault_ExcludesTheOverrideTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(["--datasource", "DesignNoFlags"], ConfigureOptions);

        context.Model.FindEntityType(typeof(FeatureFlagOverride)).Should().BeNull(
            "a migrations project that never opts in must keep scaffolding exactly what it did before");
    }

    // -- Strongly typed identifiers at design time (ADR-115) --
    // `dotnet ef` builds the model through this helper, never through the host, so the registry
    // AddStronglyTypedIds puts in the host container is absent here. The options name the assemblies
    // declaring the wrappers instead, and the helper registers the registry over them.
    [Fact]
    public void StronglyTypedIdAssemblies_ByDefault_AreEmpty() =>
        new DesignTimeDbContextOptions().StronglyTypedIdAssemblies.Should().BeEmpty(
            "a migrations project that never opts in must keep scaffolding exactly what it did before");

    [Fact]
    public void AddStronglyTypedIdAssembly_ReturnsTheSameOptionsInstance()
    {
        var options = new DesignTimeDbContextOptions();

        options.AddStronglyTypedIdAssembly(typeof(DesignWrappedId).Assembly).Should().BeSameAs(
            options,
            "the method chains like AddConfigurationAssembly");
    }

    [Fact]
    public void AddStronglyTypedIdAssembly_SameAssemblyTwice_KeepsOneEntry()
    {
        var options = new DesignTimeDbContextOptions();
        var assembly = typeof(DesignWrappedId).Assembly;

        options.AddStronglyTypedIdAssembly(assembly).AddStronglyTypedIdAssembly(assembly);

        options.StronglyTypedIdAssemblies.Should().ContainSingle().Which.Should().BeSameAs(assembly);
    }

    [Fact]
    public void AddStronglyTypedIdAssembly_Null_Throws()
    {
        var act = () => new DesignTimeDbContextOptions().AddStronglyTypedIdAssembly(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("assembly");
    }

    [Fact]
    public void WithStronglyTypedIdAssembly_MapsWrapperKeyThroughConverter()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignWrapped"],
            options =>
            {
                ConfigureOptions(options);
                options.AddStronglyTypedIdAssembly(typeof(DesignWrappedId).Assembly);
            });

        var key = context.Model.FindEntityType(typeof(DesignWrappedEntity))!
            .FindProperty(nameof(DesignWrappedEntity.Id))!;

        key.GetValueConverter().Should().BeOfType<StronglyTypedIdValueConverter<DesignWrappedId, int>>(
            "the design-time model must map the wrapper exactly as the runtime model does");
        key.GetValueConverter()!.ProviderClrType.Should().Be<int>(
            "the column stays the primitive, so adopting a wrapper is not a migration");
    }

    // The empty-list path: no assembly named, so no registry is registered and the wrapper reaches
    // EF's own "the database provider does not support this type" validation, exactly as before.
    [Fact]
    public void WithoutIt_ModelBuildThrowsNamingTheProperty()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignWrappedBare"],
            ConfigureOptions);

        var act = () => context.Model.FindEntityType(typeof(DesignBareWrappedEntity));

        act.Should().Throw<InvalidOperationException>().WithMessage("*DesignBareWrappedEntity.Id*value converter*");
    }

    [Fact]
    public void CreateSqlServer_WithNoStronglyTypedIdAssemblies_LeavesAPrimitiveKeyUnconverted()
    {
        int namedAssemblies = -1;

        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignAlpha"],
            options =>
            {
                ConfigureOptions(options);
                namedAssemblies = options.StronglyTypedIdAssemblies.Count;
            });

        namedAssemblies.Should().Be(0);
        context.Model.FindEntityType(typeof(DesignAlphaEntity))!
            .FindProperty(nameof(DesignAlphaEntity.Id))!
            .GetValueConverter().Should().BeNull("an int key maps natively when no wrapper is in play");
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

    // ── PostgreSQL design time ──
    // The entry point a PostgreSQL-backed application scaffolds through; without it such an app has
    // no way to run `dotnet ef` against the framework's context at all.
    [Fact]
    public void CreatePostgreSQL_NamedSource_BuildsThePostgreSQLContextForThatSource()
    {
        using var context = DesignTimeDbContextHelper.CreatePostgreSQL(
            ["--datasource", "DesignPostgres"],
            ConfigurePostgreSQLOptions);

        context.DataSourceKey.Should().Be(new DataSourceKey(DataSource.PostgreSQL, "DesignPostgres"));
        context.Model.FindEntityType(typeof(DesignPostgreSQLEntity)).Should().NotBeNull();
        context.Model.FindEntityType(typeof(DesignSqliteEntity)).Should().BeNull(
            "each engine's model contains only the entities that route to it");
    }

    [Fact]
    public void CreatePostgreSQL_MigrationsAssembly_ReachesTheEfOptions()
    {
        using var context = DesignTimeDbContextHelper.CreatePostgreSQL(
            ["--datasource", "DesignPostgres"],
            ConfigurePostgreSQLOptions);

        RelationalOptionsExtension.Extract(context.GetService<IDbContextOptions>())
            .MigrationsAssembly.Should().Be(
                "Design.PostgreSQL.Migrations",
                "a setting that never reaches UseNpgsql leaves EF looking for migrations next to the framework context");
    }

    [Fact]
    public void CreatePostgreSQL_NoArgument_TargetsDefaultSource()
    {
        using var context = DesignTimeDbContextHelper.CreatePostgreSQL([], ConfigurePostgreSQLOptions);

        context.DataSourceKey.Should().Be(DataSourceKey.Default(DataSource.PostgreSQL));
    }

    private static void ConfigurePostgreSQLOptions(DesignTimeDbContextOptions options)
    {
        options.ConnectionStrings = new ConnectionStringSettings
        {
            PostgreSQLConnectionString = "Host=design;Database=main;Username=app",
        };
        options.DataSources["DesignPostgres"] = new DataSourceEntrySettings
        {
            PostgreSQLConnectionString = "Host=design;Database=postgres;Username=app",
            PostgreSQLMigrationsAssembly = "Design.PostgreSQL.Migrations",
        };
        options.AddConfigurationAssembly(typeof(DesignTimeDbContextHelperTests).Assembly);
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
        // Sources used only by the stored permission-grant cases, for the same cache reason.
        options.DataSources["DesignGrants"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=Grants;",
            SQLServerMigrationsAssembly = "Design.Grants.Migrations",
        };
        options.DataSources["DesignNoGrants"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=NoGrants;",
            SQLServerMigrationsAssembly = "Design.NoGrants.Migrations",
        };
        const string sharedGrantsConnection = "Server=design;Database=SharedGrants;";
        options.DataSources["DesignGrantsAlpha"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = sharedGrantsConnection,
            SQLServerMigrationsAssembly = "Design.SharedGrants.Migrations",
        };
        options.DataSources["DesignGrantsZulu"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = sharedGrantsConnection,
            SQLServerMigrationsAssembly = "Design.SharedGrants.Migrations",
        };
        // Sources used only by the feature-flag override cases, for the same cache reason.
        options.DataSources["DesignFlags"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=Flags;",
            SQLServerMigrationsAssembly = "Design.Flags.Migrations",
        };
        options.DataSources["DesignNoFlags"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=NoFlags;",
            SQLServerMigrationsAssembly = "Design.NoFlags.Migrations",
        };
        // Sources used only by the strongly typed identifier cases, for the same cache reason. Each
        // wrapper-keyed entity lives on a source of its own, so no other model ever contains it.
        options.DataSources["DesignWrapped"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=Wrapped;",
            SQLServerMigrationsAssembly = "Design.Wrapped.Migrations",
        };
        options.DataSources["DesignWrappedBare"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=WrappedBare;",
            SQLServerMigrationsAssembly = "Design.WrappedBare.Migrations",
        };
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

    /// <summary>The PostgreSQL-engine counterpart, mapped only into the PostgreSQL design-time context.</summary>
    public sealed class DesignPostgreSQLEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>An int-backed strongly typed identifier, the shape the design-time cases map.</summary>
    public readonly record struct DesignWrappedId(int Value) : IStronglyTypedId<DesignWrappedId, int>
    {
        /// <summary>Wraps a primitive key.</summary>
        public static DesignWrappedId From(int value) => new(value);
    }

    /// <summary>Keyed by a wrapper; mapped only into the source whose options name its assembly.</summary>
    public sealed class DesignWrappedEntity : AuditableAggregateRootEntity<DesignWrappedId>
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>Keyed by the same wrapper, on a source whose options name no identifier assembly.</summary>
    public sealed class DesignBareWrappedEntity : AuditableAggregateRootEntity<DesignWrappedId>
    {
        public string Name { get; set; } = string.Empty;
    }

    [UseDatabase("DesignWrapped")]
    private sealed class DesignWrappedEntityConfiguration : EntityTypeConfigurationSQLServer<DesignWrappedEntity, DesignWrappedId>;

    [UseDatabase("DesignWrappedBare")]
    private sealed class DesignBareWrappedEntityConfiguration : EntityTypeConfigurationSQLServer<DesignBareWrappedEntity, DesignWrappedId>;

    [UseDatabase("DesignAlpha")]
    private sealed class DesignAlphaEntityConfiguration : EntityTypeConfigurationSQLServer<DesignAlphaEntity, int>;

    [UseDatabase("DesignBeta")]
    private sealed class DesignBetaEntityConfiguration : EntityTypeConfigurationSQLServer<DesignBetaEntity, int>;

    [UseDatabase("DesignSqlite")]
    private sealed class DesignSqliteEntityConfiguration : EntityTypeConfigurationSqlite<DesignSqliteEntity, int>;

    [UseDatabase("DesignPostgres")]
    private sealed class DesignPostgreSQLEntityConfiguration : EntityTypeConfigurationPostgreSQL<DesignPostgreSQLEntity, int>;
}
