using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Gcp.CloudSql;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using Npgsql;

namespace MMCA.Common.Gcp.Tests.CloudSql;

/// <summary>
/// <c>AddCloudSqlIamAuthentication</c>: off unless <c>CloudSql:IamAuthentication:Enabled</c> is true,
/// and when on it registers one data-source configurator whose periodic password provider hands Npgsql
/// an OAuth2 access token. The token source is injected, so nothing here reaches Google; the real
/// data-source path against a server is the PostgreSQL Testcontainers tier.
/// </summary>
public sealed class CloudSqlIamAuthenticationTests
{
    private const string CloudSqlHost = "10.20.0.3";
    private const string EncryptedConnectionString = $"Host={CloudSqlHost};Port=5432;Database=orders;Username=orders-api@my-project.iam;SSL Mode=Require";

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static IConfiguration EnabledConfiguration(Dictionary<string, string?> settings)
    {
        settings["CloudSql:IamAuthentication:Enabled"] = "true";
        return ConfigurationWith(settings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void AddCloudSqlIamAuthentication_WhenNotEnabled_RegistersNothing(string? enabled)
    {
        var services = new ServiceCollection();

        services.AddCloudSqlIamAuthentication(ConfigurationWith(new()
        {
            ["CloudSql:IamAuthentication:Enabled"] = enabled,
            ["ConnectionStrings:PostgreSQLConnectionString"] = "Host=localhost;Database=app;Username=app;Password=secret",
        }));

        services.Should().NotContain(d => d.ServiceType == typeof(INpgsqlDataSourceConfigurator));
        services.Should().NotContain(d => d.ServiceType == typeof(NpgsqlDataSource));
    }

    [Fact]
    public void AddCloudSqlIamAuthentication_WhenEnabled_RegistersTheConfiguratorAndTheDataSource()
    {
        var services = new ServiceCollection();

        services.AddCloudSqlIamAuthentication(EnabledConfiguration(new()
        {
            ["ConnectionStrings:PostgreSQLConnectionString"] = EncryptedConnectionString,
        }));

        using var provider = services.BuildServiceProvider();
        provider.GetServices<INpgsqlDataSourceConfigurator>().Should().ContainSingle()
            .Which.Should().BeOfType<CloudSqlIamAuthenticationConfigurator>();

        // Building it is what applies the configurator. No credential is loaded here: Application
        // Default Credentials are read on the first token request, not at registration or build.
        provider.GetKeyedService<NpgsqlDataSource>(EncryptedConnectionString).Should().NotBeNull();
    }

    [Fact]
    public void AddCloudSqlIamAuthentication_CalledTwice_RegistersOneConfigurator()
    {
        var services = new ServiceCollection();
        var configuration = EnabledConfiguration(new() { ["ConnectionStrings:PostgreSQLConnectionString"] = EncryptedConnectionString });

        services.AddCloudSqlIamAuthentication(configuration);
        services.AddCloudSqlIamAuthentication(configuration);

        services.Count(d => d.ServiceType == typeof(INpgsqlDataSourceConfigurator)).Should().Be(1);
    }

    [Fact]
    public async Task ThePasswordProvider_ReturnsTheTokenFromTheInjectedSource()
    {
        var requests = 0;
        var configurator = new CloudSqlIamAuthenticationConfigurator(_ =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult("ya29.fake-access-token");
        });

        var password = await configurator.GetPasswordAsync(
            new NpgsqlConnectionStringBuilder(EncryptedConnectionString),
            TestContext.Current.CancellationToken);

        password.Should().Be("ya29.fake-access-token");
        requests.Should().Be(1);
    }

    [Fact]
    public void TheRefreshInterval_StaysInsideTheCredentialsOwnRefreshWindow()
    {
        // Google's credential caches its token and refreshes it in the background only once fewer than
        // 225 seconds remain (it blocks for a new one under 60), and Npgsql reuses whatever it was handed
        // for the whole interval. An interval longer than about 112 seconds can therefore hold a token
        // past its expiry; the hour-long token lifetime is not the bound.
        CloudSqlIamAuthenticationConfigurator.TokenRefreshInterval.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(112));
        CloudSqlIamAuthenticationConfigurator.FailureRetryInterval.Should().Be(TimeSpan.FromSeconds(30));
        CloudSqlAccessTokenSource.Scope.Should().Be("https://www.googleapis.com/auth/sqlservice.login");
    }

    [Theory]
    [InlineData("Require")]
    [InlineData("VerifyCA")]
    [InlineData("VerifyFull")]
    public void AnEncryptedConnectionString_IsAccepted(string sslMode)
    {
        var services = new ServiceCollection();

        var act = () => services.AddCloudSqlIamAuthentication(EnabledConfiguration(new()
        {
            ["ConnectionStrings:PostgreSQLConnectionString"] = $"Host={CloudSqlHost};Database=orders;Username=orders-api@my-project.iam;SSL Mode={sslMode}",
        }));

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData(";SSL Mode=Prefer")]
    [InlineData(";SSL Mode=Allow")]
    [InlineData(";SSL Mode=Disable")]
    public void AMissingOrWeakSslMode_FailsRegistrationNamingTheKeyAndTheFix(string sslSuffix)
    {
        var services = new ServiceCollection();

        var act = () => services.AddCloudSqlIamAuthentication(EnabledConfiguration(new()
        {
            ["DataSources:Orders:PostgreSQLConnectionString"] = $"Host={CloudSqlHost};Database=orders;Username=orders-api@my-project.iam{sslSuffix}",
        }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CloudSql:IamAuthentication:Enabled*")
            .WithMessage("*DataSources:Orders:PostgreSQLConnectionString*")
            .WithMessage("*SSL Mode=Require*");
    }

    [Fact]
    public void AWeakSslMode_AlsoFailsTheDataSourceBuild_ForAConnectionStringRegistrationNeverSaw()
    {
        var configurator = new CloudSqlIamAuthenticationConfigurator(_ => Task.FromResult("token"));

        var act = () => configurator.Configure(new NpgsqlDataSourceBuilder($"Host={CloudSqlHost};Database=orders;Username=orders-api@my-project.iam"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*SSL Mode=Require*");
    }

    [Fact]
    public void AConnectionStringThatCarriesAPassword_IsRefusedWhenItsDataSourceIsBuilt()
    {
        // The token IS the password, so a static one alongside it is a configuration error; Npgsql
        // refuses a data source with both, which is the same behaviour the RDS adapter relies on.
        var configurator = new CloudSqlIamAuthenticationConfigurator(_ => Task.FromResult("token"));
        var builder = new NpgsqlDataSourceBuilder(EncryptedConnectionString + ";Password=static-secret");

        var act = () =>
        {
            configurator.Configure(builder);
            using var dataSource = builder.Build();
        };

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void OtherEngines_AreLeftAlone()
    {
        var services = new ServiceCollection();

        var act = () => services.AddCloudSqlIamAuthentication(EnabledConfiguration(new()
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=10.20.0.4;Database=app;Encrypt=false",
            ["ConnectionStrings:SqliteConnectionString"] = "Data Source=app.db",
            ["DataSources:Catalog:CosmosConnectionString"] = "AccountEndpoint=https://cosmos.example:443/;AccountKey=a2V5",
        }));

        act.Should().NotThrow("IAM database authentication applies to PostgreSQL connection strings only");
    }

    [Fact]
    public void Configure_RejectsANullBuilder_AndTheConfiguratorANullTokenSource()
    {
        var noBuilder = () => new CloudSqlIamAuthenticationConfigurator(_ => Task.FromResult("token")).Configure(null!);
        var noSource = () => new CloudSqlIamAuthenticationConfigurator(null!);

        noBuilder.Should().Throw<ArgumentNullException>();
        noSource.Should().Throw<ArgumentNullException>();
    }
}
