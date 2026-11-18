using Amazon;
using Amazon.Runtime;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Aws.Rds;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Tests.Shared.Hosting;
using Npgsql;

namespace MMCA.Common.Aws.Tests.Rds;

/// <summary>
/// <c>AddAwsRdsIamAuthentication</c>: off unless <c>Rds:IamAuthentication:Enabled</c> is true, and when
/// on it registers one data-source configurator that swaps the static password for a periodically
/// refreshed RDS IAM token. The gate, registration and SSL-mode cases shared with the Cloud SQL and
/// Entra adapters run from <see cref="PostgreSqlTokenAuthenticationTestsBase"/>; the
/// cases below are RDS-only. Token signing is local SigV4 over fake static keys, so nothing here
/// reaches AWS; the real data-source path against a server is the PostgreSQL Testcontainers tier.
/// </summary>
public sealed class AwsRdsIamAuthenticationTests : PostgreSqlTokenAuthenticationTestsBase
{
    private const string RdsHost = "orders.cluster-abc123.us-east-1.rds.amazonaws.com";

    protected override string EnabledKey => "Rds:IamAuthentication:Enabled";

    protected override string ServerHost => RdsHost;

    protected override string UserName => "app_iam";

    protected override void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddAwsRdsIamAuthentication(configuration);

    protected override INpgsqlDataSourceConfigurator CreateConfigurator() =>
        new RdsIamAuthenticationConfigurator(region: null, credentials: null);

    protected override IConfiguration EnabledConfiguration(Dictionary<string, string?> settings)
    {
        settings["Rds:IamAuthentication:Region"] = "us-east-1";
        settings.TryAdd("Rds:IamAuthentication:AccessKeyId", "AKIDEXAMPLE");
        settings.TryAdd("Rds:IamAuthentication:SecretAccessKey", "fake-secret-key");
        return base.EnabledConfiguration(settings);
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
}
