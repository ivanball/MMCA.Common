using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Gcp.CloudSql;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Tests.Shared.Hosting;
using Npgsql;

namespace MMCA.Common.Gcp.Tests.CloudSql;

/// <summary>
/// <c>AddCloudSqlIamAuthentication</c>: off unless <c>CloudSql:IamAuthentication:Enabled</c> is true,
/// and when on it registers one data-source configurator whose periodic password provider hands Npgsql
/// an OAuth2 access token. The gate, registration and SSL-mode cases shared with the RDS and Entra
/// adapters run from <see cref="PostgreSqlTokenAuthenticationTestsBase"/>; the cases below are Cloud
/// SQL-only. The token source is injected, so nothing here reaches Google; the real data-source path
/// against a server is the PostgreSQL Testcontainers tier.
/// </summary>
public sealed class CloudSqlIamAuthenticationTests : PostgreSqlTokenAuthenticationTestsBase
{
    private const string CloudSqlHost = "10.20.0.3";

    protected override string EnabledKey => "CloudSql:IamAuthentication:Enabled";

    protected override string ServerHost => CloudSqlHost;

    protected override string UserName => "orders-api@my-project.iam";

    protected override void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddCloudSqlIamAuthentication(configuration);

    protected override INpgsqlDataSourceConfigurator CreateConfigurator() =>
        new CloudSqlIamAuthenticationConfigurator(_ => Task.FromResult("token"));

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
    public void Configure_RejectsANullBuilder_AndTheConfiguratorANullTokenSource()
    {
        var noBuilder = () => new CloudSqlIamAuthenticationConfigurator(_ => Task.FromResult("token")).Configure(null!);
        var noSource = () => new CloudSqlIamAuthenticationConfigurator(null!);

        noBuilder.Should().Throw<ArgumentNullException>();
        noSource.Should().Throw<ArgumentNullException>();
    }
}
