using Amazon;
using Amazon.Runtime;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Aws.Rds;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Tests.Shared.Hosting;
using Npgsql;

namespace MMCA.Common.Aws.Tests.Rds;

/// <summary>
/// <c>AddAwsRdsIamAuthentication</c>: off unless <c>Rds:IamAuthentication:Enabled</c> is true, and when
/// on it registers one data-source configurator that swaps the static password for a periodically
/// refreshed RDS IAM token, plus the receipt the startup tripwire checks. The gate, registration and SSL-mode cases shared with the Cloud SQL and
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

    protected override void AssertNothingRegistered(IServiceCollection services) =>
        services.Should().BeEmpty("a host that leaves the gate off takes no AWS credential, no configurator and no receipt");

    protected override void AssertEnabledRegistrations(IServiceCollection services) =>
        services.Should().ContainSingle(d => IsTheRdsMarker(d));

    private static bool IsTheRdsMarker(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(HostIntegrationMarker)
        && descriptor.ImplementationInstance is HostIntegrationMarker marker
        && marker.Name == HostIntegrationNames.AwsRdsIamAuthentication;

    // As for the Entra twin: a keyed registration of the marker type is skipped, not read, and it
    // does not count as the receipt.
    [Fact]
    public void AKeyedMarker_NeitherThrowsNorCounts()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(
            "some-key",
            new HostIntegrationMarker(HostIntegrationNames.AwsRdsIamAuthentication));

        var act = () => services.AddAwsRdsIamAuthentication(
            EnabledConfiguration(new() { ["ConnectionStrings:PostgreSQLConnectionString"] = EncryptedConnectionString }));

        act.Should().NotThrow();
        services.Should().ContainSingle(d => !d.IsKeyedService && IsTheRdsMarker(d), "the keyed one is not the receipt");
    }

    [Fact]
    public void CalledTwice_LeavesOneReceipt()
    {
        var services = new ServiceCollection();
        var configuration = EnabledConfiguration(new() { ["ConnectionStrings:PostgreSQLConnectionString"] = EncryptedConnectionString });

        services.AddAwsRdsIamAuthentication(configuration);
        services.AddAwsRdsIamAuthentication(configuration);

        services.Should().ContainSingle(d => IsTheRdsMarker(d));
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
