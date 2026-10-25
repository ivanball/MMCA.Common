using System.Reflection;
using AwesomeAssertions;
using HealthChecks.NpgSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MMCA.Common.Aspire.Tests.Health;

/// <summary>
/// The PostgreSQL readiness check and the data source it probes. When the container holds an
/// <see cref="NpgsqlDataSource"/> keyed by the connection string (what Infrastructure registers once a
/// data-source configurator is added, e.g. RDS IAM tokens), the check must probe THAT instance, because
/// a check built from the bare connection string would authenticate without the password provider and
/// fail against a healthy database. Without one, the check is exactly the connection-string check it
/// always was.
/// </summary>
public sealed class PostgreSqlHealthCheckDataSourceTests
{
    private const string ConnectionString = "Host=db.example;Database=app;Username=app";

    [Fact]
    public void WithoutAKeyedDataSource_TheCheckIsThePlainConnectionStringCheck()
    {
        using var provider = BuildProvider(keyedDataSource: null);
        var registration = PostgresRegistration(provider);

        var options = OptionsOf(registration.Factory(provider));

        options.ConnectionString.Should().Be(ConnectionString, "the package's own connection-string check is kept as is");
        options.DataSource.Should().BeNull();
    }

    [Fact]
    public void WithAKeyedDataSource_TheCheckProbesThatInstance()
    {
        using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        using var provider = BuildProvider(dataSource);
        var registration = PostgresRegistration(provider);

        OptionsOf(registration.Factory(provider)).DataSource.Should().BeSameAs(dataSource);
        OptionsOf(registration.Factory(provider)).DataSource.Should().BeSameAs(dataSource);
    }

    [Fact]
    public void TheRegistrationKeepsItsNameAndTags()
    {
        using var provider = BuildProvider(keyedDataSource: null);

        var registration = PostgresRegistration(provider);

        registration.Name.Should().Be("postgresql");
        registration.Tags.Should().BeEmpty("no database check is tagged optional; they all gate readiness");
    }

    private static ServiceProvider BuildProvider(NpgsqlDataSource? keyedDataSource)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSQLConnectionString"] = ConnectionString,
        });

        builder.AddInfrastructureHealthChecks(requireDatabase: true);

        if (keyedDataSource is not null)
        {
            builder.Services.AddKeyedSingleton(ConnectionString, keyedDataSource);
        }

        return builder.Services.BuildServiceProvider();
    }

    private static HealthCheckRegistration PostgresRegistration(ServiceProvider provider) =>
        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Single(r => r.Name == "postgresql");

    private static NpgSqlHealthCheckOptions OptionsOf(IHealthCheck check)
    {
        check.Should().BeOfType<NpgSqlHealthCheck>();

        // The check keeps its options private; read the one field of that type.
        return (NpgSqlHealthCheckOptions)typeof(NpgSqlHealthCheck)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(NpgSqlHealthCheckOptions))
            .GetValue(check)!;
    }
}
