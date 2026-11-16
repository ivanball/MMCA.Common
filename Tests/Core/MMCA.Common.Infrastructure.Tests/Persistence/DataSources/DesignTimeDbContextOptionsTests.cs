using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Design;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

/// <summary>
/// Unit tests for <see cref="DesignTimeDbContextOptions.UseSqlServerServiceDatabase"/>, the one call
/// that declares a service's own database for its migrations factory. Asserted on the resulting
/// options: the selected data source name and the single named entry (connection string and
/// migrations assembly). Each test reads its own uniquely named environment variable and clears it
/// afterwards, so the tests need no non-parallel collection.
/// </summary>
public sealed class DesignTimeDbContextOptionsTests
{
    private const string LocalConnectionString =
        "Server=localhost;Database=Svc_Catalog;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

    private static readonly Assembly MigrationsAssembly = typeof(DesignTimeDbContextOptionsTests).Assembly;

    [Fact]
    public void UseSqlServerServiceDatabase_VariableUnset_UsesTheLocalDatabase()
    {
        var variable = UniqueVariable();
        var options = new DesignTimeDbContextOptions();

        options.UseSqlServerServiceDatabase("Catalog", variable, "Svc_Catalog", MigrationsAssembly);

        options.DataSourceName.Should().Be("Catalog");
        var entry = options.DataSources.Should().ContainSingle().Subject;
        entry.Key.Should().Be("Catalog");
        entry.Value.SQLServerConnectionString.Should().Be(LocalConnectionString);
        entry.Value.SQLServerMigrationsAssembly.Should().Be(MigrationsAssembly.GetName().Name);
    }

    [Fact]
    public void UseSqlServerServiceDatabase_VariableSet_UsesItsConnectionString()
    {
        var variable = UniqueVariable();
        const string fromEnvironment = "Server=ci-sql;Database=Svc_Catalog_Ci;User Id=sa;Password=x;TrustServerCertificate=True";
        Environment.SetEnvironmentVariable(variable, fromEnvironment);
        try
        {
            var options = new DesignTimeDbContextOptions();

            options.UseSqlServerServiceDatabase("Catalog", variable, "Svc_Catalog", MigrationsAssembly);

            options.DataSourceName.Should().Be("Catalog");
            var entry = options.DataSources.Should().ContainSingle().Subject;
            entry.Key.Should().Be("Catalog");
            entry.Value.SQLServerConnectionString.Should().Be(fromEnvironment);
            entry.Value.SQLServerMigrationsAssembly.Should().Be(MigrationsAssembly.GetName().Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void UseSqlServerServiceDatabase_DeclaresNoTopLevelConnectionString()
    {
        // The single named entry must stay the only source, so DataSourceResolver promotes it to
        // Default exactly as it does in the running host.
        var options = new DesignTimeDbContextOptions();

        options.UseSqlServerServiceDatabase("Catalog", UniqueVariable(), "Svc_Catalog", MigrationsAssembly);

        options.ConnectionStrings.Should().BeEquivalentTo(new DesignTimeDbContextOptions().ConnectionStrings);
    }

    [Fact]
    public void UseSqlServerServiceDatabase_ReturnsTheSameOptions()
    {
        var options = new DesignTimeDbContextOptions();

        options.UseSqlServerServiceDatabase("Catalog", UniqueVariable(), "Svc_Catalog", MigrationsAssembly)
            .Should().BeSameAs(options);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void UseSqlServerServiceDatabase_BlankStringArgument_Throws(string? blank)
    {
        var options = new DesignTimeDbContextOptions();
        var variable = UniqueVariable();

        FluentActions.Invoking(() => options.UseSqlServerServiceDatabase(blank!, variable, "Svc_Catalog", MigrationsAssembly))
            .Should().Throw<ArgumentException>().WithParameterName("dataSourceName");
        FluentActions.Invoking(() => options.UseSqlServerServiceDatabase("Catalog", blank!, "Svc_Catalog", MigrationsAssembly))
            .Should().Throw<ArgumentException>().WithParameterName("connectionStringVariable");
        FluentActions.Invoking(() => options.UseSqlServerServiceDatabase("Catalog", variable, blank!, MigrationsAssembly))
            .Should().Throw<ArgumentException>().WithParameterName("localDatabaseName");

        options.DataSourceName.Should().BeNull();
        options.DataSources.Should().BeEmpty();
    }

    [Fact]
    public void UseSqlServerServiceDatabase_NullAssembly_Throws()
    {
        var options = new DesignTimeDbContextOptions();

        FluentActions.Invoking(() => options.UseSqlServerServiceDatabase("Catalog", UniqueVariable(), "Svc_Catalog", null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("migrationsAssembly");

        options.DataSources.Should().BeEmpty();
    }

    private static string UniqueVariable() =>
        "MMCA_DESIGN_TIME_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
}
