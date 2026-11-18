using AwesomeAssertions;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Azure.PostgreSql;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Tests.Shared.Hosting;
using Npgsql;

namespace MMCA.Common.Azure.Tests.PostgreSql;

/// <summary>
/// Item 33, <c>AddAzurePostgreSqlEntraAuthentication</c> (the Azure twin of
/// <c>MMCA.Common.Gcp.Tests/CloudSql/CloudSqlIamAuthenticationTests.cs</c>): off unless
/// <c>AzurePostgreSql:EntraAuthentication:Enabled</c> is true, and when on it registers one data-source
/// configurator whose periodic password provider hands Npgsql a Microsoft Entra access token for the
/// Azure Database for PostgreSQL scope, plus the receipt the startup tripwire checks. The gate,
/// registration and SSL-mode cases shared with the RDS and Cloud SQL adapters run from
/// <see cref="PostgreSqlTokenAuthenticationTestsBase"/>; the cases below are Entra-only, plus the one
/// copy of the Npgsql static-password refusal all three adapters rely on. The token source is
/// injected, so nothing here reaches Azure; no Azure Database for PostgreSQL emulator exists.
/// </summary>
public sealed class AzurePostgreSqlEntraAuthenticationTests : PostgreSqlTokenAuthenticationTestsBase
{
    private const string EntraScope = "https://ossrdbms-aad.database.windows.net/.default";

    protected override string EnabledKey => "AzurePostgreSql:EntraAuthentication:Enabled";

    protected override string ServerHost => "orders.postgres.database.azure.com";

    protected override string UserName => "orders-api";

    protected override void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddAzurePostgreSqlEntraAuthentication(configuration);

    protected override INpgsqlDataSourceConfigurator CreateConfigurator() =>
        new AzurePostgreSqlEntraAuthenticationConfigurator(_ => Task.FromResult("token"));

    protected override void AssertNothingRegistered(IServiceCollection services) =>
        services.Should().BeEmpty("a host that leaves the gate off takes no Azure credential and no configurator");

    protected override void AssertEnabledRegistrations(IServiceCollection services) =>
        services.Should().ContainSingle(d => IsTheEntraMarker(d));

    private static bool IsTheEntraMarker(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(HostIntegrationMarker)
        && descriptor.ImplementationInstance is HostIntegrationMarker marker
        && marker.Name == HostIntegrationNames.AzurePostgreSqlEntraAuthentication;

    // Ruling L3: a keyed registration of the marker type is skipped, not read (reading
    // ImplementationInstance on a keyed descriptor throws), and it does not count as the receipt.
    [Fact]
    public void AKeyedMarker_NeitherThrowsNorCounts()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(
            "some-key",
            new HostIntegrationMarker(HostIntegrationNames.AzurePostgreSqlEntraAuthentication));

        var act = () => services.AddAzurePostgreSqlEntraAuthentication(
            EnabledConfiguration(new() { ["ConnectionStrings:PostgreSQLConnectionString"] = EncryptedConnectionString }));

        act.Should().NotThrow();
        services.Should().ContainSingle(d => !d.IsKeyedService && IsTheEntraMarker(d), "the keyed one is not the receipt");
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
    public void AConnectionStringThatCarriesAPassword_IsRefusedWhenItsDataSourceIsBuilt()
    {
        // The token IS the password, so a static one alongside it is a configuration error. Npgsql
        // refuses a data source with both (provider behaviour, asserted rather than implemented), the
        // same behaviour the Cloud SQL and RDS adapters rely on, so this is its only copy.
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
