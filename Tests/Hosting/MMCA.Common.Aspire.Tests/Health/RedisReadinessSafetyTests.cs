using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Caching;

namespace MMCA.Common.Aspire.Tests.Health;

/// <summary>
/// The regression gate for the 2026-09-02 readiness outage. Aspire's Redis integrations register the
/// AspNetCore.HealthChecks.Redis check under the name <c>StackExchange.Redis</c> with NO tags, and the
/// readiness predicate in <c>MapDefaultEndpoints()</c> admits every check that is not tagged
/// <c>live</c> or <c>optional</c>. That untagged check therefore gated readiness, issued
/// <c>CLUSTER INFO</c> against Azure Managed Redis (which StackExchange.Redis 3.x detects as a
/// cluster and which refuses the command outside admin mode), and took every replica of every backend
/// service out of rotation while the caches were perfectly healthy.
/// <para>
/// These assertions are deliberately phrased against the readiness CONTRACT rather than against one
/// registration name: whatever a future Aspire version decides to register, no Redis check may reach
/// <c>/health/ready</c>.
/// </para>
/// </summary>
public sealed class RedisReadinessSafetyTests
{
    // abortConnect=false so nothing in this file ever tries to reach a real server.
    private const string RedisConnectionString = "localhost:6379,abortConnect=false,connectTimeout=1";

    [Fact]
    public void AddRedisCaching_DoesNotRegisterTheUntaggedAspireRedisCheck()
    {
        var builder = BuilderWithRedis();

        builder.AddRedisCaching();
        builder.AddInfrastructureHealthChecks();

        Registrations(builder).Select(r => r.Name)
            .Should().NotContain(
                name => name.StartsWith("StackExchange.Redis", StringComparison.OrdinalIgnoreCase),
                because: "the Aspire integration's own check is untagged, so its presence alone makes Redis readiness-fatal");
    }

    [Fact]
    public void EveryRedisCheck_IsTaggedOptional_SoNoneOfThemGateReadiness()
    {
        var builder = BuilderWithRedis();

        builder.AddRedisCaching();
        builder.AddInfrastructureHealthChecks();

        var redisChecks = Registrations(builder)
            .Where(r => r.Name.Contains("redis", StringComparison.OrdinalIgnoreCase))
            .ToList();

        redisChecks.Should().NotBeEmpty("Redis must still be observable on /health");
        redisChecks.Should().AllSatisfy(r => r.Tags.Should().Contain(HealthCheckTags.Optional));
    }

    [Fact]
    public void ReadinessPredicate_AdmitsNoRedisCheck()
    {
        var builder = BuilderWithRedis();

        builder.AddRedisCaching();
        builder.AddRedisOutputCaching();
        builder.AddInfrastructureHealthChecks();

        // The exact predicate MapDefaultEndpoints() applies to /health/ready.
        var readinessChecks = Registrations(builder)
            .Where(r => !r.Tags.Contains(HealthCheckTags.Live) && !r.Tags.Contains(HealthCheckTags.Optional))
            .Select(r => r.Name)
            .ToList();

        readinessChecks.Should().NotContain(
            name => name.Contains("redis", StringComparison.OrdinalIgnoreCase),
            because: "a cache the application falls back to memory without must never be able to unready every replica at once");
    }

    [Fact]
    public void AddRedisCaching_WithoutAConnectionString_RegistersNothing()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>());

        var act = () => builder.AddRedisCaching();

        act.Should().NotThrow("Redis is optional per host and its absence is a valid configuration");
        builder.Services.Should().NotContain(
            d => d.ServiceType == typeof(StackExchange.Redis.IConnectionMultiplexer),
            because: "a host with no Redis connection string must not acquire a client that can never connect");
    }

    [Fact]
    public void AddRedisOutputCaching_WithoutAConnectionString_RegistersNothing()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>());
        var before = builder.Services.Count;

        builder.AddRedisOutputCaching();

        builder.Services.Count.Should().Be(
            before,
            because: "with no Redis the built-in per-replica memory store is correct and must stay in place");
    }

    private static HostApplicationBuilder BuilderWithRedis()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = RedisConnectionString,
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=(local);Database=Test;Integrated Security=true;TrustServerCertificate=true",
        });

        return builder;
    }

    private static IReadOnlyList<HealthCheckRegistration> Registrations(HostApplicationBuilder builder) =>
        [.. builder.Services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations];
}
