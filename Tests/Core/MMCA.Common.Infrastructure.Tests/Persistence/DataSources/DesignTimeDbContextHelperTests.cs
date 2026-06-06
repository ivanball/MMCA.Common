using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Design;
using MMCA.Common.Infrastructure.Settings;

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

    [UseDatabase("DesignAlpha")]
    private sealed class DesignAlphaEntityConfiguration : EntityTypeConfigurationSQLServer<DesignAlphaEntity, int>;

    [UseDatabase("DesignBeta")]
    private sealed class DesignBetaEntityConfiguration : EntityTypeConfigurationSQLServer<DesignBetaEntity, int>;
}
