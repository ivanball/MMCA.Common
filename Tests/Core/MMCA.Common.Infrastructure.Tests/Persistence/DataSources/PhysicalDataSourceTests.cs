using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

/// <summary>
/// The rule that decides, per physical source, whether startup migrates the database or creates it
/// outright. It is one property because three call sites depend on the same answer: the context
/// factory's migrate and pending-migration passes, and the API layer's initialization strategy.
/// </summary>
public sealed class PhysicalDataSourceTests
{
    // SQL Server has been migration-driven since the first release, INCLUDING the single-database
    // monolith whose Default source names no migrations assembly and lets EF look next to the
    // context. Tying SQL Server to a configured assembly would stop migrating those hosts.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("App.Migrations")]
    public void UsesMigrations_SqlServer_IsAlwaysTrue(string? migrationsAssembly)
    {
        var source = new PhysicalDataSource(
            DataSourceKey.Default(DataSource.SQLServer),
            "Server=test;Database=test",
            migrationsAssembly,
            string.Empty);

        source.UsesMigrations.Should().BeTrue();
    }

    [Fact]
    public void UsesMigrations_SqliteWithAMigrationsAssembly_IsTrue()
    {
        var source = new PhysicalDataSource(
            new DataSourceKey(DataSource.Sqlite, "Tickets"),
            "Data Source=tickets.db",
            "Tickets.Migrations.Sqlite",
            string.Empty);

        source.UsesMigrations.Should().BeTrue();
    }

    // Backward compatibility: a SQLite source wired by hand before the setting existed has no
    // migrations to apply, so it must keep being created outright rather than migrated into nothing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UsesMigrations_SqliteWithoutAMigrationsAssembly_IsFalse(string? migrationsAssembly)
    {
        var source = new PhysicalDataSource(
            new DataSourceKey(DataSource.Sqlite, "Tickets"),
            "Data Source=tickets.db",
            migrationsAssembly,
            string.Empty);

        source.UsesMigrations.Should().BeFalse();
    }

    // PostgreSQL follows the SQLite rule, not the SQL Server one: it ships with no host that already
    // depends on being migrated, so a source that names no migrations assembly has nothing to apply
    // and must be created outright rather than migrated into an empty schema.
    [Fact]
    public void UsesMigrations_PostgreSQLWithAMigrationsAssembly_IsTrue()
    {
        var source = new PhysicalDataSource(
            new DataSourceKey(DataSource.PostgreSQL, "Tickets"),
            "Host=localhost;Database=tickets;Username=app",
            "Tickets.Migrations.PostgreSQL",
            string.Empty);

        source.UsesMigrations.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UsesMigrations_PostgreSQLWithoutAMigrationsAssembly_IsFalse(string? migrationsAssembly)
    {
        var source = new PhysicalDataSource(
            new DataSourceKey(DataSource.PostgreSQL, "Tickets"),
            "Host=localhost;Database=tickets;Username=app",
            migrationsAssembly,
            string.Empty);

        source.UsesMigrations.Should().BeFalse();
    }

    [Fact]
    public void UsesMigrations_Cosmos_IsFalse()
    {
        var source = new PhysicalDataSource(
            DataSourceKey.Default(DataSource.CosmosDB),
            "AccountEndpoint=https://test;AccountKey=dGVzdA==",
            null,
            "MMCA");

        source.UsesMigrations.Should().BeFalse();
    }
}
