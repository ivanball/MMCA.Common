using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Aspire.Tests.Health;

/// <summary>
/// Guards the one asymmetry in <c>AddInfrastructureHealthChecks</c>: the relational database can
/// fail fast on a missing connection string while Redis and RabbitMQ silently skip. That difference
/// is deliberate (an optional cache is a valid configuration, a host that cannot resolve its own
/// database is not) and it is exactly the kind of inconsistency a later refactor "tidies away", so
/// it is asserted directly rather than left to review.
/// </summary>
public sealed class InfrastructureHealthChecksTests
{
    [Fact]
    public void AddInfrastructureHealthChecks_WhenDatabaseNotRequiredAndMissing_SkipsSilently()
    {
        var builder = BuilderWith([]);

        var act = () => builder.AddInfrastructureHealthChecks();

        act.Should().NotThrow();
        RegisteredCheckNames(builder).Should().NotContain("sqlserver");
    }

    [Fact]
    public void AddInfrastructureHealthChecks_WhenSqlConfigured_RegistersTheCheck()
    {
        var builder = BuilderWith(new()
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=(local);Database=Test;Integrated Security=true;TrustServerCertificate=true",
        });

        builder.AddInfrastructureHealthChecks(requireDatabase: true);

        RegisteredCheckNames(builder).Should().Contain("sqlserver");
    }

    [Fact]
    public void AddInfrastructureHealthChecks_WhenSqlDeclaredOnlyUnderDataSources_SatisfiesTheRequirement()
    {
        // A database-per-service host declares its database under DataSources, not at the top level.
        var builder = BuilderWith(new()
        {
            ["DataSources:Tickets:SQLServerConnectionString"] = "Server=(local);Database=Tickets;Integrated Security=true;TrustServerCertificate=true",
        });

        var act = () => builder.AddInfrastructureHealthChecks(requireDatabase: true);

        act.Should().NotThrow();
        RegisteredCheckNames(builder).Should().Contain(
            "sqlserver",
            because: "the first declared database keeps the plain engine check name whichever section declares it");
    }

    // Absent Redis/RabbitMQ must never throw, whatever the database flag is: they are optional per host.
    [Fact]
    public void AddInfrastructureHealthChecks_WithOnlySqlConfigured_DoesNotRegisterOptionalDependencies()
    {
        var builder = BuilderWith(new()
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=(local);Database=Test;Integrated Security=true;TrustServerCertificate=true",
        });

        builder.AddInfrastructureHealthChecks(requireDatabase: true);

        var names = RegisteredCheckNames(builder);
        names.Should().NotContain("redis");
        names.Should().NotContain("rabbitmq");
    }

    // The readiness contract, and the reason it is asserted rather than assumed: /health/ready
    // includes every check NOT tagged live or optional. If the Redis check were untagged it would
    // gate readiness, and a Redis blip would take EVERY replica out of rotation at once, converting
    // a graceful degradation (DistributedCacheService falls back to MemoryCacheService) into a
    // total outage. The database is the opposite case: untagged on purpose, because a host that
    // cannot reach its own database cannot serve correct responses.
    [Fact]
    public void OptionalDependencies_AreTaggedOptional_SoTheyDoNotGateReadiness()
    {
        var builder = BuilderWith(new()
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=(local);Database=Test;Integrated Security=true;TrustServerCertificate=true",
            ["ConnectionStrings:redis"] = "localhost:6379",
        });

        builder.AddInfrastructureHealthChecks(requireDatabase: true);

        var registrations = Registrations(builder);

        registrations.Single(r => r.Name == "redis").Tags
            .Should().Contain(HealthCheckTags.Optional,
                because: "the app falls back to an in-memory cache, so a Redis outage must not pull every replica from traffic");

        registrations.Single(r => r.Name == "sqlserver").Tags
            .Should().NotContain(HealthCheckTags.Optional,
                because: "a host that cannot reach its own database cannot serve correct responses, so SQL must gate readiness");
    }

    // ── SQLite: the small-application engine gets a real check, not a silent skip ──
    [Fact]
    public void AddInfrastructureHealthChecks_WhenSqliteConfigured_RegistersTheCheck()
    {
        var builder = BuilderWith(new()
        {
            ["ConnectionStrings:SqliteConnectionString"] = "Data Source=app.db",
        });

        builder.AddInfrastructureHealthChecks();

        RegisteredCheckNames(builder).Should().Contain(
            "sqlite",
            because: "a host whose only database is a SQLite file is no less dependent on it than a SQL Server host");
    }

    [Fact]
    public void SqliteCheck_IsNotTaggedOptional_SoItGatesReadiness()
    {
        var builder = BuilderWith(new()
        {
            ["ConnectionStrings:SqliteConnectionString"] = "Data Source=app.db",
        });

        builder.AddInfrastructureHealthChecks();

        Registrations(builder).Single(r => r.Name == "sqlite").Tags
            .Should().NotContain(HealthCheckTags.Optional,
                because: "an unreadable database file means the host cannot answer a single query");
    }

    [Fact]
    public void AddInfrastructureHealthChecks_WhenDatabaseRequiredAndSqliteConfigured_DoesNotThrow()
    {
        // requireDatabase is engine-agnostic: an application that picks its engine from configuration
        // demands A database without naming SQL Server.
        var builder = BuilderWith(new()
        {
            ["ConnectionStrings:SqliteConnectionString"] = "Data Source=app.db",
        });

        var act = () => builder.AddInfrastructureHealthChecks(requireDatabase: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddInfrastructureHealthChecks_WhenDatabaseRequiredAndNoneConfigured_Throws()
    {
        var builder = BuilderWith([]);

        var act = () => builder.AddInfrastructureHealthChecks(requireDatabase: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SQLServerConnectionString*")
            .WithMessage("*SqliteConnectionString*");
    }

    [Fact]
    public void AddInfrastructureHealthChecks_WhenSqliteAbsent_SkipsSilently()
    {
        var builder = BuilderWith(new()
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=(local);Database=Test;Integrated Security=true;TrustServerCertificate=true",
        });

        builder.AddInfrastructureHealthChecks(requireDatabase: true);

        RegisteredCheckNames(builder).Should().NotContain(
            "sqlite",
            because: "a SQL Server host must not acquire a second, always-failing database check");
    }

    private static IReadOnlyList<HealthCheckRegistration> Registrations(HostApplicationBuilder builder) =>
        [.. builder.Services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations];

    private static HostApplicationBuilder BuilderWith(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    private static IReadOnlyList<string> RegisteredCheckNames(HostApplicationBuilder builder) =>
        [.. builder.Services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Select(r => r.Name)];
}
