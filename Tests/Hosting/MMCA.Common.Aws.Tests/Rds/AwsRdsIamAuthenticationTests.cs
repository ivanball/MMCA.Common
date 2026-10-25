using Amazon;
using Amazon.Runtime;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Aws.Rds;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using Npgsql;

namespace MMCA.Common.Aws.Tests.Rds;

/// <summary>
/// <c>AddAwsRdsIamAuthentication</c>: off unless <c>Rds:IamAuthentication:Enabled</c> is true, and when
/// on it registers one data-source configurator that swaps the static password for a periodically
/// refreshed RDS IAM token. Token signing is local SigV4 over fake static keys, so nothing here reaches
/// AWS; the real data-source path against a server is the PostgreSQL Testcontainers tier.
/// </summary>
public sealed class AwsRdsIamAuthenticationTests
{
    private const string RdsHost = "orders.cluster-abc123.us-east-1.rds.amazonaws.com";
    private const string EncryptedConnectionString = $"Host={RdsHost};Port=5432;Database=orders;Username=app_iam;SSL Mode=Require";

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void AddAwsRdsIamAuthentication_WhenNotEnabled_RegistersNothing(string? enabled)
    {
        var services = new ServiceCollection();

        services.AddAwsRdsIamAuthentication(ConfigurationWith(new()
        {
            ["Rds:IamAuthentication:Enabled"] = enabled,
            ["ConnectionStrings:PostgreSQLConnectionString"] = "Host=localhost;Database=app;Username=app;Password=secret",
        }));

        services.Should().NotContain(d => d.ServiceType == typeof(INpgsqlDataSourceConfigurator));
        services.Should().NotContain(d => d.ServiceType == typeof(NpgsqlDataSource));
    }

    [Fact]
    public void AddAwsRdsIamAuthentication_WhenEnabled_RegistersTheConfiguratorAndTheDataSource()
    {
        var services = new ServiceCollection();

        services.AddAwsRdsIamAuthentication(EnabledConfiguration(new()
        {
            ["ConnectionStrings:PostgreSQLConnectionString"] = EncryptedConnectionString,
        }));

        using var provider = services.BuildServiceProvider();
        provider.GetServices<INpgsqlDataSourceConfigurator>().Should().ContainSingle()
            .Which.Should().BeOfType<RdsIamAuthenticationConfigurator>();

        // Building it is what applies the configurator: a periodic password provider plus a static
        // password would make Npgsql refuse the build.
        provider.GetKeyedService<NpgsqlDataSource>(EncryptedConnectionString).Should().NotBeNull();
    }

    [Fact]
    public void AddAwsRdsIamAuthentication_CalledTwice_RegistersOneConfigurator()
    {
        var services = new ServiceCollection();
        var configuration = EnabledConfiguration(new() { ["ConnectionStrings:PostgreSQLConnectionString"] = EncryptedConnectionString });

        services.AddAwsRdsIamAuthentication(configuration);
        services.AddAwsRdsIamAuthentication(configuration);

        services.Count(d => d.ServiceType == typeof(INpgsqlDataSourceConfigurator)).Should().Be(1);
    }

    [Fact]
    public async Task TheTokenCallback_SignsAConnectTokenForTheConnectionStringsHostPortAndUser()
    {
        var configurator = new RdsIamAuthenticationConfigurator(
            RegionEndpoint.USEast1,
            new BasicAWSCredentials("AKIDEXAMPLE", "fake-secret-key"));

        var token = await configurator.GenerateTokenAsync(
            new NpgsqlConnectionStringBuilder(EncryptedConnectionString),
            TestContext.Current.CancellationToken);

        token.Should().NotBeNullOrWhiteSpace();
        token.Should().StartWith($"{RdsHost}:5432/?", "the token is a presigned connect request for that endpoint");
        token.Should().Contain("Action=connect");
        token.Should().Contain("DBUser=app_iam");
        token.Should().Contain("X-Amz-Signature=");
        token.Should().Contain("us-east-1%2Frds-db%2Faws4_request", "it is signed for the rds-db service in the configured region");
    }

    [Theory]
    [InlineData("Require")]
    [InlineData("VerifyCA")]
    [InlineData("VerifyFull")]
    public void AnEncryptedConnectionString_IsAccepted(string sslMode)
    {
        var services = new ServiceCollection();

        var act = () => services.AddAwsRdsIamAuthentication(EnabledConfiguration(new()
        {
            ["ConnectionStrings:PostgreSQLConnectionString"] = $"Host={RdsHost};Database=orders;Username=app_iam;SSL Mode={sslMode}",
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

        var act = () => services.AddAwsRdsIamAuthentication(EnabledConfiguration(new()
        {
            ["DataSources:Orders:PostgreSQLConnectionString"] = $"Host={RdsHost};Database=orders;Username=app_iam{sslSuffix}",
        }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DataSources:Orders:PostgreSQLConnectionString*")
            .WithMessage("*SSL Mode=Require*");
    }

    [Fact]
    public void AWeakSslMode_AlsoFailsTheDataSourceBuild_ForAConnectionStringRegistrationNeverSaw()
    {
        var configurator = new RdsIamAuthenticationConfigurator(region: null, credentials: null);

        var act = () => configurator.Configure(new NpgsqlDataSourceBuilder($"Host={RdsHost};Database=orders;Username=app_iam"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*SSL Mode=Require*");
    }

    [Fact]
    public void OtherEngines_AreLeftAlone()
    {
        var services = new ServiceCollection();

        var act = () => services.AddAwsRdsIamAuthentication(EnabledConfiguration(new()
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=sql.example;Database=app;Encrypt=false",
            ["ConnectionStrings:SqliteConnectionString"] = "Data Source=app.db",
            ["DataSources:Catalog:CosmosConnectionString"] = "AccountEndpoint=https://cosmos.example:443/;AccountKey=a2V5",
        }));

        act.Should().NotThrow("IAM database authentication applies to PostgreSQL connection strings only");
    }

    [Theory]
    [InlineData("AKIDEXAMPLE", null)]
    [InlineData(null, "fake-secret-key")]
    public void AHalfSetStaticKeyPair_FailsRegistration(string? accessKeyId, string? secretAccessKey)
    {
        var services = new ServiceCollection();

        var act = () => services.AddAwsRdsIamAuthentication(EnabledConfiguration(new()
        {
            ["Rds:IamAuthentication:AccessKeyId"] = accessKeyId,
            ["Rds:IamAuthentication:SecretAccessKey"] = secretAccessKey,
        }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Rds:IamAuthentication:AccessKeyId*");
    }

    private static IConfiguration EnabledConfiguration(Dictionary<string, string?> settings)
    {
        settings["Rds:IamAuthentication:Enabled"] = "true";
        settings["Rds:IamAuthentication:Region"] = "us-east-1";
        settings.TryAdd("Rds:IamAuthentication:AccessKeyId", "AKIDEXAMPLE");
        settings.TryAdd("Rds:IamAuthentication:SecretAccessKey", "fake-secret-key");
        return ConfigurationWith(settings);
    }
}
