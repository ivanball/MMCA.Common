using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MMCA.Common.Aspire.Health;
using Moq;
using StackExchange.Redis;

namespace MMCA.Common.Aspire.Tests.Health;

/// <summary>
/// The replacement for the AspNetCore.HealthChecks.Redis check must answer the only question a health
/// probe has ("can this process talk to Redis?") with the only command that is always available:
/// <c>PING</c>. The check it replaced branched on server type and issued <c>CLUSTER INFO</c>, which
/// Azure Managed Redis refuses outside admin mode, so it reported a healthy cache as failed forever.
/// </summary>
public sealed class RedisPingHealthCheckTests
{
    private const string ConnectionString = "localhost:6379,abortConnect=false,connectTimeout=1";

    [Fact]
    public async Task CheckHealthAsync_WhenPingSucceeds_ReportsHealthy()
    {
        var database = new Mock<IDatabase>(MockBehavior.Strict);
        database.Setup(d => d.PingAsync(CommandFlags.None)).ReturnsAsync(TimeSpan.FromMilliseconds(3));

        await using var check = CheckWith(database.Object);

        var result = await check.CheckHealthAsync(Context(check), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
        database.VerifyAll();
    }

    [Fact]
    public async Task CheckHealthAsync_UsesPingOnly_AndNeverAnAdministrativeCommand()
    {
        // MockBehavior.Strict is the assertion: any call other than the configured PingAsync throws,
        // so a future edit that reaches for CLUSTER INFO, INFO, or a server endpoint fails here.
        var database = new Mock<IDatabase>(MockBehavior.Strict);
        database.Setup(d => d.PingAsync(CommandFlags.None)).ReturnsAsync(TimeSpan.FromMilliseconds(1));

        var multiplexer = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        multiplexer.Setup(m => m.GetDatabase(-1, null)).Returns(database.Object);

        await using var check = new RedisPingHealthCheck(ConnectionString, Provider(multiplexer.Object));

        var result = await check.CheckHealthAsync(Context(check), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
        multiplexer.VerifyAll();
    }

    [Fact]
    public async Task CheckHealthAsync_WhenPingThrows_ReportsUnhealthyWithoutRethrowing()
    {
        var database = new Mock<IDatabase>();
        database.Setup(d => d.PingAsync(It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(
                ConnectionFailureType.UnableToConnect,
                CommandFlags.None,
                "no route to host",
                innerException: null,
                commandStatus: CommandStatus.Unknown));

        await using var check = CheckWith(database.Object);

        var result = await check.CheckHealthAsync(Context(check), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<RedisConnectionException>();
    }

    // The exact shape of the failure the incident produced: the server answering a command with
    // "admin mode is not enabled". It must degrade the /health payload, never fault the probe.
    [Fact]
    public async Task CheckHealthAsync_WhenTheServerRefusesACommand_ReportsUnhealthyWithoutRethrowing()
    {
        var database = new Mock<IDatabase>();
        database.Setup(d => d.PingAsync(It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisCommandException("This operation is not available unless admin mode is enabled: CLUSTER"));

        await using var check = CheckWith(database.Object);

        var result = await check.CheckHealthAsync(Context(check), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<RedisCommandException>();
    }

    private static RedisPingHealthCheck CheckWith(IDatabase database)
    {
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(database);

        return new RedisPingHealthCheck(ConnectionString, Provider(multiplexer.Object));
    }

    private static ServiceProvider Provider(IConnectionMultiplexer multiplexer) =>
        new ServiceCollection().AddSingleton(multiplexer).BuildServiceProvider();

    private static HealthCheckContext Context(IHealthCheck check) => new()
    {
        Registration = new HealthCheckRegistration(
            "redis",
            check,
            failureStatus: null,
            tags: [HealthCheckTags.Optional]),
    };
}
