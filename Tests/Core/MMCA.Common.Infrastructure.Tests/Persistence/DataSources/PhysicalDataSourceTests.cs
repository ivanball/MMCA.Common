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
            null,
            string.Empty)
        {
            SqliteMigrationsAssembly = "Tickets.Migrations.Sqlite",
        };

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
            null,
            string.Empty)
        {
            SqliteMigrationsAssembly = migrationsAssembly,
        };

        source.UsesMigrations.Should().BeFalse();
    }

    // The SQL Server slot is never consulted for a SQLite source: the two are kept apart precisely
    // so a mixed host cannot hand one engine's snapshot to the other.
    [Fact]
    public void UsesMigrations_SqliteCarryingOnlyTheSqlServerAssembly_IsFalse()
    {
        var source = new PhysicalDataSource(
            new DataSourceKey(DataSource.Sqlite, "Tickets"),
            "Data Source=tickets.db",
            "Main.Migrations",
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
            "AtlDevCon");

        source.UsesMigrations.Should().BeFalse();
    }
}
