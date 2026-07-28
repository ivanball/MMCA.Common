using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Aspire.Tests.Health;

/// <summary>
/// Guards the one asymmetry in <c>AddInfrastructureHealthChecks</c>: SQL Server can fail fast on a
/// missing connection string while Redis and RabbitMQ silently skip. That difference is deliberate
/// (an optional cache is a valid configuration, a host that cannot resolve its own database is not)
/// and it is exactly the kind of inconsistency a later refactor "tidies away", so it is asserted
/// directly rather than left to review.
/// </summary>
public sealed class InfrastructureHealthChecksTests
{
    [Fact]
    public void AddInfrastructureHealthChecks_WhenSqlRequiredAndMissing_ThrowsAtStartup()
    {
        var builder = BuilderWith([]);

        var act = () => builder.AddInfrastructureHealthChecks(requireSqlServer: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SQLServerConnectionString*");
    }

    [Fact]
    public void AddInfrastructureHealthChecks_WhenSqlNotRequiredAndMissing_SkipsSilently()
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

        builder.AddInfrastructureHealthChecks(requireSqlServer: true);

        RegisteredCheckNames(builder).Should().Contain("sqlserver");
    }

    // Absent Redis/RabbitMQ must never throw, whatever the SQL flag is: they are optional per host.
    [Fact]
    public void AddInfrastructureHealthChecks_WithOnlySqlConfigured_DoesNotRegisterOptionalDependencies()
    {
        var builder = BuilderWith(new()
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=(local);Database=Test;Integrated Security=true;TrustServerCertificate=true",
        });

        builder.AddInfrastructureHealthChecks(requireSqlServer: true);

        var names = RegisteredCheckNames(builder);
        names.Should().NotContain("redis");
        names.Should().NotContain("rabbitmq");
    }

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
