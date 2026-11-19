using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Health;
using Npgsql;

namespace MMCA.Common.Aspire.Tests.Health;

/// <summary>
/// The PostgreSQL readiness check and the data source it probes. When the container holds an
/// <see cref="NpgsqlDataSource"/> keyed by the connection string (what Infrastructure registers once a
/// data-source configurator is added, e.g. RDS IAM tokens), the check must probe THAT instance, because
/// a check built from the bare connection string would authenticate without the password provider and
/// fail against a healthy database. Without one, the check probes a data source of its own, built from
/// the connection string once and disposed with the container.
/// </summary>
public sealed class PostgreSqlHealthCheckDataSourceTests
{
    private const string ConnectionString = "Host=db.example;Database=app;Username=app";

    [Fact]
    public void WithoutAKeyedDataSource_TheCheckProbesItsOwnDataSource_BuiltOnce()
    {
        using var provider = BuildProvider(keyedDataSource: null);
        var check = CheckOf(provider);

        var first = check.DataSourceForProbe();

        first.ConnectionString.Should().Be(ConnectionString, "the owned data source is built from the connection string");
        check.DataSourceForProbe().Should().BeSameAs(first, "the owned data source is reused across probes");
        CheckOf(provider).Should().BeSameAs(check, "the check is a singleton, so its data source lives as long as the host");
    }

    [Fact]
    public void WithAKeyedDataSource_TheCheckProbesThatInstance()
    {
        using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        using var provider = BuildProvider(dataSource);
        var check = CheckOf(provider);

        check.DataSourceForProbe().Should().BeSameAs(dataSource);
        check.DataSourceForProbe().Should().BeSameAs(dataSource);
    }

    [Fact]
    public void AKeyedDataSourceRegisteredAfterTheCheck_IsStillTheOneProbed()
    {
        // Decided per probe: the registrations can arrive in either order.
        using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        var builder = BuilderWithPostgres();
        builder.AddInfrastructureHealthChecks(requireDatabase: true);
        builder.Services.AddKeyedSingleton(ConnectionString, dataSource);
        using var provider = builder.Services.BuildServiceProvider();

        CheckOf(provider).DataSourceForProbe().Should().BeSameAs(dataSource);
    }

    [Fact]
    public async Task DisposingTheContainer_DisposesTheOwnedDataSource_ButNotTheSharedOne()
    {
        await using var shared = NpgsqlDataSource.Create(ConnectionString);
        var ownedProvider = BuildProvider(keyedDataSource: null);
        var owned = CheckOf(ownedProvider).DataSourceForProbe();
        var sharedProvider = BuildProvider(shared);
        CheckOf(sharedProvider).DataSourceForProbe();

        await ownedProvider.DisposeAsync();
        await sharedProvider.DisposeAsync();

        var openOwned = async () => await owned.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await openOwned.Should().ThrowAsync<ObjectDisposedException>("the check owns the data source it built");
        shared.CreateCommand().Should().NotBeNull("the shared data source belongs to the container that registered it");
    }

    [Fact]
    public void TheRegistrationKeepsItsNameTagsFailureStatusAndTimeout()
    {
        using var provider = BuildProvider(keyedDataSource: null);

        var registration = PostgresRegistration(provider);

        registration.Name.Should().Be("postgresql");
        registration.Tags.Should().BeEmpty("no database check is tagged optional; they all gate readiness");
        registration.FailureStatus.Should().Be(HealthStatus.Unhealthy);
        registration.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        registration.Factory(provider).Should().BeOfType<PostgreSqlHealthCheck>();
    }

    private static HostApplicationBuilder BuilderWithPostgres()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSQLConnectionString"] = ConnectionString,
        });
        return builder;
    }

    private static ServiceProvider BuildProvider(NpgsqlDataSource? keyedDataSource)
    {
        var builder = BuilderWithPostgres();

        if (keyedDataSource is not null)
        {
            builder.Services.AddKeyedSingleton(ConnectionString, keyedDataSource);
        }

        builder.AddInfrastructureHealthChecks(requireDatabase: true);

        return builder.Services.BuildServiceProvider();
    }

    private static HealthCheckRegistration PostgresRegistration(ServiceProvider provider) =>
        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Single(r => r.Name == "postgresql");

    private static PostgreSqlHealthCheck CheckOf(ServiceProvider provider) =>
        PostgresRegistration(provider).Factory(provider).Should().BeOfType<PostgreSqlHealthCheck>().Subject;
}
