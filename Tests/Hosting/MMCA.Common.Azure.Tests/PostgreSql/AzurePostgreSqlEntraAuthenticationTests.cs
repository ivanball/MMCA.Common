using Azure.Core;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Azure.PostgreSql;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using Npgsql;

namespace MMCA.Common.Azure.Tests.PostgreSql;

/// <summary>
/// Item 33, <c>AddAzurePostgreSqlEntraAuthentication</c> (the Azure twin of
/// <c>MMCA.Common.Gcp.Tests/CloudSql/CloudSqlIamAuthenticationTests.cs</c>): off unless
/// <c>AzurePostgreSql:EntraAuthentication:Enabled</c> is true, and when on it registers one data-source
/// configurator whose periodic password provider hands Npgsql a Microsoft Entra access token for the
/// Azure Database for PostgreSQL scope, plus the receipt the startup tripwire checks. The token source
/// is injected, so nothing here reaches Azure; no Azure Database for PostgreSQL emulator exists.
/// </summary>
public sealed class AzurePostgreSqlEntraAuthenticationTests
{
    private const string ServerHost = "orders.postgres.database.azure.com";
    private const string EncryptedConnectionString = $"Host={ServerHost};Port=5432;Database=orders;Username=orders-api;SSL Mode=Require";
    private const string EntraScope = "https://ossrdbms-aad.database.windows.net/.default";

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static IConfiguration EnabledConfiguration(Dictionary<string, string?> settings)
    {
        settings["AzurePostgreSql:EntraAuthentication:Enabled"] = "true";
        return ConfigurationWith(settings);
    }

    private static bool IsTheEntraMarker(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(HostIntegrationMarker)
        && descriptor.ImplementationInstance is HostIntegrationMarker marker
        && marker.Name == HostIntegrationNames.AzurePostgreSqlEntraAuthentication;

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void WhenNotEnabled_RegistersNothing(string? enabled)
    {
        var services = new ServiceCollection();

        // A weak SSL mode on purpose: with the gate off nothing is validated either.
        services.AddAzurePostgreSqlEntraAuthentication(ConfigurationWith(new()
        {
            ["AzurePostgreSql:EntraAuthentication:Enabled"] = enabled,
            ["ConnectionStrings:PostgreSQLConnectionString"] = "Host=localhost;Database=app;Username=app;Password=secret",
        }));

        services.Should().BeEmpty("a host that leaves the gate off takes no Azure credential and no configurator");
    }

    [Fact]
    public void WhenEnabled_RegistersTheConfiguratorTheMarkerAndTheDataSource()
    {
        var services = new ServiceCollection();

        services.AddAzurePostgreSqlEntraAuthentication(EnabledConfiguration(new()
        {
            ["ConnectionStrings:PostgreSQLConnectionString"] = EncryptedConnectionString,
        }));

        services.Should().ContainSingle(d => IsTheEntraMarker(d));

        using var provider = services.BuildServiceProvider();
        provider.GetServices<INpgsqlDataSourceConfigurator>().Should().ContainSingle()
            .Which.Should().BeOfType<AzurePostgreSqlEntraAuthenticationConfigurator>();

        // Building it is what applies the configurator. No token is requested here: the credential is
        // asked for one on the first connection, not at registration or build.
        provider.GetKeyedService<NpgsqlDataSource>(EncryptedConnectionString).Should().NotBeNull();
    }

    [Fact]
    public void CalledTwice_RegistersOneConfigurator()
    {
        var services = new ServiceCollection();
        var configuration = EnabledConfiguration(new() { ["ConnectionStrings:PostgreSQLConnectionString"] = EncryptedConnectionString });

        services.AddAzurePostgreSqlEntraAuthentication(configuration);
        services.AddAzurePostgreSqlEntraAuthentication(configuration);

        services.Count(d => d.ServiceType == typeof(INpgsqlDataSourceConfigurator)).Should().Be(1);
    }

    [Fact]
    public async Task ThePasswordProvider_ReturnsTheTokenFromTheInjectedSource()
    {
        var requests = 0;
        var configurator = new AzurePostgreSqlEntraAuthenticationConfigurator(_ =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult("fake-entra-password-token");
        });

        var password = await configurator.GetPasswordAsync(
            new NpgsqlConnectionStringBuilder(EncryptedConnectionString),
            TestContext.Current.CancellationToken);

        password.Should().Be("fake-entra-password-token");
        requests.Should().Be(1);
    }

    [Fact]
    public async Task ThePasswordProvider_HonoursCancellation()
    {
        // Rule 6: a cancelled request does not reach the token source.
        var requests = 0;
        var configurator = new AzurePostgreSqlEntraAuthenticationConfigurator(_ =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult("token");
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await configurator.GetPasswordAsync(new NpgsqlConnectionStringBuilder(EncryptedConnectionString), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        requests.Should().Be(0);
    }

    [Fact]
    public void TheRefreshInterval_IsInsideTheCredentialRefreshMargin()
    {
        // Azure.Identity refreshes a cached token once fewer than five minutes remain, and Npgsql reuses
        // whatever it was handed for the whole interval, so the interval must stay under five minutes.
        AzurePostgreSqlEntraAuthenticationConfigurator.TokenRefreshInterval.Should().BeLessThan(TimeSpan.FromMinutes(5));
        AzurePostgreSqlEntraAuthenticationConfigurator.TokenRefreshInterval.Should().BePositive();
        AzurePostgreSqlEntraAuthenticationConfigurator.FailureRetryInterval.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheScope_IsTheAzureDatabaseForPostgreSqlScope()
    {
        AzurePostgreSqlAccessTokenSource.Scope.Should().Be(EntraScope);

        var credential = new RecordingTokenCredential("fake-entra-access-token");
        var source = new AzurePostgreSqlAccessTokenSource(credential);

        credential.Requests.Should().BeEmpty("no token is requested until one is asked for");

        var token = await source.GetTokenAsync(TestContext.Current.CancellationToken);

        token.Should().Be("fake-entra-access-token");
        credential.Requests.Should().ContainSingle().Which.Should().Equal(EntraScope);
    }

    [Theory]
    [InlineData("Require")]
    [InlineData("VerifyCA")]
    [InlineData("VerifyFull")]
    public void AnEncryptedConnectionString_IsAccepted(string sslMode)
    {
        var services = new ServiceCollection();

        var act = () => services.AddAzurePostgreSqlEntraAuthentication(EnabledConfiguration(new()
        {
            ["ConnectionStrings:PostgreSQLConnectionString"] = $"Host={ServerHost};Database=orders;Username=orders-api;SSL Mode={sslMode}",
            ["DataSources:Orders:PostgreSQLConnectionString"] = $"Host={ServerHost};Database=orders;Username=orders-api;SSL Mode={sslMode}",
        }));

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("ConnectionStrings:PostgreSQLConnectionString", "")]
    [InlineData("ConnectionStrings:PostgreSQLConnectionString", ";SSL Mode=Prefer")]
    [InlineData("DataSources:Orders:PostgreSQLConnectionString", "")]
    [InlineData("DataSources:Orders:PostgreSQLConnectionString", ";SSL Mode=Allow")]
    [InlineData("DataSources:Orders:PostgreSQLConnectionString", ";SSL Mode=Disable")]
    public void AMissingOrWeakSslMode_FailsRegistrationNamingTheKeyAndTheFix(string key, string sslSuffix)
    {
        var services = new ServiceCollection();

        var act = () => services.AddAzurePostgreSqlEntraAuthentication(EnabledConfiguration(new()
        {
            [key] = $"Host={ServerHost};Database=orders;Username=orders-api{sslSuffix}",
        }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AzurePostgreSql:EntraAuthentication*")
            .WithMessage($"*{key}*")
            .WithMessage("*SSL Mode=Require*");
    }

    [Fact]
    public void AWeakSslMode_AlsoFailsTheDataSourceBuild()
    {
        // A connection string registration never saw (supplied later) is still checked when its data
        // source is built.
        var configurator = new AzurePostgreSqlEntraAuthenticationConfigurator(_ => Task.FromResult("token"));

        var act = () => configurator.Configure(new NpgsqlDataSourceBuilder($"Host={ServerHost};Database=orders;Username=orders-api"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*SSL Mode=Require*");
    }

    [Fact]
    public void AConnectionStringThatCarriesAPassword_IsRefusedWhenItsDataSourceIsBuilt()
    {
        // The token IS the password, so a static one alongside it is a configuration error. Npgsql
        // refuses a data source with both (provider behaviour, asserted rather than implemented), the
        // same behaviour the Cloud SQL and RDS adapters rely on.
        var configurator = new AzurePostgreSqlEntraAuthenticationConfigurator(_ => Task.FromResult("token"));
        var builder = new NpgsqlDataSourceBuilder(EncryptedConnectionString + ";Password=static-placeholder");

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

        var act = () => services.AddAzurePostgreSqlEntraAuthentication(EnabledConfiguration(new()
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=10.20.0.4;Database=app;Encrypt=false",
            ["ConnectionStrings:SqliteConnectionString"] = "Data Source=app.db",
            ["DataSources:Catalog:CosmosConnectionString"] = "AccountEndpoint=https://cosmos.example:443/;AccountKey=a2V5",
        }));

        act.Should().NotThrow("Entra token login applies to PostgreSQL connection strings only");
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        var noConfiguration = () => new ServiceCollection().AddAzurePostgreSqlEntraAuthentication(null!);
        var noBuilder = () => new AzurePostgreSqlEntraAuthenticationConfigurator(_ => Task.FromResult("token")).Configure(null!);
        var noSource = () => new AzurePostgreSqlEntraAuthenticationConfigurator(null!);
        var noSettings = async () => await new AzurePostgreSqlEntraAuthenticationConfigurator(_ => Task.FromResult("token"))
            .GetPasswordAsync(null!, TestContext.Current.CancellationToken);

        noConfiguration.Should().Throw<ArgumentNullException>();
        noBuilder.Should().Throw<ArgumentNullException>();
        noSource.Should().Throw<ArgumentNullException>();
        await noSettings.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>A credential that records the scopes of every token request and returns a fixed token.</summary>
    private sealed class RecordingTokenCredential(string token) : TokenCredential
    {
        public List<string[]> Requests { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(requestContext.Scopes);
            }

            return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
