using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Health;
using Moq;
using RabbitMQ.Client;

namespace MMCA.Common.Aspire.Tests.Health;

/// <summary>
/// The framework's own SQL Server, PostgreSQL, SQLite and RabbitMQ readiness checks. Each registration
/// keeps the name, tags, failure status and timeout the checks always had, and a probe that cannot
/// reach its dependency reports the registration's failure status with the exception instead of
/// throwing; only a cancellation of the probe's own token propagates. Unreachable endpoints are local
/// closed ports; nothing leaves the machine.
/// </summary>
public sealed class DependencyHealthCheckTests
{
    [Fact]
    public async Task EveryRegistration_KeepsItsNameTagsFailureStatusAndTimeout()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=(local);Database=Test;Integrated Security=true;TrustServerCertificate=true",
            ["ConnectionStrings:PostgreSQLConnectionString"] = "Host=localhost;Database=test;Username=test",
            ["ConnectionStrings:SqliteConnectionString"] = "Data Source=app.db",
            ["ConnectionStrings:rabbitmq"] = "amqp://guest:guest@localhost:5672/",
            ["HealthChecks:CheckTimeoutSeconds"] = "9",
        });
        builder.AddInfrastructureHealthChecks(requireDatabase: true);
        // DisposeAsync: the RabbitMQ connection holder is async-disposable only.
        await using var provider = builder.Services.BuildServiceProvider();

        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.ToDictionary(r => r.Name);

        AssertRegistration<SqlServerHealthCheck>(registrations["sqlserver"], provider, optional: false);
        AssertRegistration<PostgreSqlHealthCheck>(registrations["postgresql"], provider, optional: false);
        AssertRegistration<SqliteHealthCheck>(registrations["sqlite"], provider, optional: false);
        AssertRegistration<RabbitMqHealthCheck>(registrations["rabbitmq"], provider, optional: true);
    }

    [Fact]
    public async Task SqlServer_WhenTheServerIsUnreachable_ReportsTheFailureStatus()
    {
        var check = new SqlServerHealthCheck(
            $"Server=tcp:127.0.0.1,{ClosedPort()};Database=test;User ID=test;Password=test;Connect Timeout=2;TrustServerCertificate=true");

        var result = await check.CheckHealthAsync(DegradedContext(check), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded, "the check reports the registration's failure status");
        result.Exception.Should().NotBeNull();
    }

    [Fact]
    public async Task SqlServer_WhenTheProbeIsCancelled_PropagatesTheCancellation()
    {
        var check = new SqlServerHealthCheck(
            $"Server=tcp:127.0.0.1,{ClosedPort()};Database=test;User ID=test;Password=test;Connect Timeout=2");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => check.CheckHealthAsync(DegradedContext(check), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("the health check service owns a cancelled probe");
    }

    [Fact]
    public async Task PostgreSql_WhenTheServerIsUnreachable_ReportsTheFailureStatus()
    {
        await using var check = new PostgreSqlHealthCheck(
            $"Host=127.0.0.1;Port={ClosedPort()};Database=test;Username=test;Password=test;Timeout=2",
            new ServiceCollection().BuildServiceProvider());

        var result = await check.CheckHealthAsync(DegradedContext(check), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Exception.Should().NotBeNull();
    }

    [Fact]
    public async Task Sqlite_WhenTheDatabaseOpens_ReportsHealthy()
    {
        var check = new SqliteHealthCheck("Data Source=:memory:");

        var result = await check.CheckHealthAsync(DegradedContext(check), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy, result.Exception?.ToString());
    }

    [Fact]
    public async Task Sqlite_WhenTheDatabaseCannotBeOpened_ReportsTheFailureStatus()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.db");
        var check = new SqliteHealthCheck($"Data Source={missing};Mode=ReadOnly");

        var result = await check.CheckHealthAsync(DegradedContext(check), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Exception.Should().NotBeNull();
    }

    [Fact]
    public async Task RabbitMq_WhenAChannelOpens_ReportsHealthy_AndClosesTheChannel()
    {
        var channel = new Mock<IChannel>();
        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        await using var holder = new RabbitMqHealthConnection(_ => Task.FromResult(connection.Object), TimeSpan.FromSeconds(5));
        var check = new RabbitMqHealthCheck(holder);

        var result = await check.CheckHealthAsync(DegradedContext(check), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
        channel.Verify(c => c.DisposeAsync(), Times.Once(), "the probe's channel is closed, never leaked");
        connection.Verify(c => c.DisposeAsync(), Times.Never(), "the held connection outlives the probe");
    }

    [Fact]
    public async Task RabbitMq_WhenTheConnectionCannotBeCreated_ReportsTheFailureStatus()
    {
        await using var holder = new RabbitMqHealthConnection(
            _ => Task.FromException<IConnection>(new InvalidOperationException("broker unreachable")),
            TimeSpan.FromSeconds(5));
        var check = new RabbitMqHealthCheck(holder);

        var result = await check.CheckHealthAsync(DegradedContext(check), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    private static void AssertRegistration<TCheck>(HealthCheckRegistration registration, IServiceProvider provider, bool optional)
        where TCheck : IHealthCheck
    {
        registration.Factory(provider).Should().BeOfType<TCheck>();
        registration.FailureStatus.Should().Be(HealthStatus.Unhealthy, "check '{0}' keeps the default failure status", registration.Name);
        registration.Timeout.Should().Be(TimeSpan.FromSeconds(9), "check '{0}' is bounded by HealthChecks:CheckTimeoutSeconds", registration.Name);
        if (optional)
        {
            registration.Tags.Should().ContainSingle().Which.Should().Be(HealthCheckTags.Optional);
        }
        else
        {
            registration.Tags.Should().BeEmpty("database check '{0}' gates readiness", registration.Name);
        }
    }

    /// <summary>A context whose registration fails as Degraded, so the result proves where the status came from.</summary>
    private static HealthCheckContext DegradedContext(IHealthCheck check) => new()
    {
        Registration = new HealthCheckRegistration("probe", check, HealthStatus.Degraded, tags: null),
    };

    /// <summary>A loopback port nothing listens on: bound by the OS, then released.</summary>
    private static string ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
