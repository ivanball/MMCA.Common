using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire;
using MMCA.Common.Aspire.Caching;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Azure.Redis;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Caching;
using StackExchange.Redis;

namespace MMCA.Common.Azure.Tests.Redis;

/// <summary>
/// Security lane D1, <c>AddAzureRedisEntraAuthentication</c>: off unless
/// <c>AzureRedis:EntraAuthentication:Enabled</c> is true; when on it registers one Redis configurator
/// and the receipt the startup tripwire checks, and the configurator refuses a connection string that
/// carries a password (access key) or turns TLS off. The credential is a local fake that hands out a
/// fixed unsigned token, so no test here requests a real token or opens a connection.
/// </summary>
public sealed class AzureRedisEntraAuthenticationTests
{
    private const string EnabledKey = "AzureRedis:EntraAuthentication:Enabled";
    private const string ObjectId = "00000000-0000-0000-0000-0000000000d1";
    private const string TlsConnectionString = "orders.redis.cache.windows.net:6380,ssl=true,abortConnect=false";

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Registration_GateOff_RegistersNothing(string? enabled)
    {
        var services = new ServiceCollection();

        services.AddAzureRedisEntraAuthentication(ConfigurationWith(new() { [EnabledKey] = enabled }));

        services.Should().BeEmpty("a host that leaves the gate off takes no Azure credential and no configurator");
    }

    [Fact]
    public void Registration_GateOn_RegistersOneConfiguratorAndTheReceipt()
    {
        var services = new ServiceCollection();

        services.AddAzureRedisEntraAuthentication(ConfigurationWith(new() { [EnabledKey] = "true" }));

        services.Should().ContainSingle(d => d.ServiceType == typeof(IRedisConfigurationOptionsConfigurator))
            .Which.ImplementationInstance.Should().BeOfType<AzureRedisEntraAuthenticationConfigurator>();
        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(HostIntegrationMarker)
            && d.ImplementationInstance is HostIntegrationMarker
            && ((HostIntegrationMarker)d.ImplementationInstance).Name == HostIntegrationNames.AzureRedisEntraAuthentication);
    }

    [Fact]
    public void Startup_GateOnWithTheRegistration_PassesTheTripwire()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [EnabledKey] = "true",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = string.Empty,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
        });
        builder.AddServiceDefaults();
        builder.Services.AddAzureRedisEntraAuthentication(builder.Configuration);

        using var host = builder.Build();
        var validate = () => host.Services.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().NotThrow();
    }

    [Fact]
    public void Configure_TlsAndNoPassword_SignsInWithTheCredentialToken()
    {
        var credential = new FakeTokenCredential(ObjectId);
        var configurator = new AzureRedisEntraAuthenticationConfigurator(credential);
        var options = ConfigurationOptions.Parse(TlsConnectionString);

        configurator.Configure(options);

        credential.Requests.Should().BePositive("the sign-in takes its token from the configured credential");
        options.Password.Should().Be(credential.Token, "the Entra access token is the password");
        options.Ssl.Should().BeTrue();
        options.EndPoints.Select(EndPointCollection.ToString).Should().Equal("orders.redis.cache.windows.net:6380");
    }

    [Theory]
    [InlineData(TlsConnectionString + ",password=the-access-key")]
    [InlineData(TlsConnectionString + ",user=default,password=the-access-key")]
    public void Configure_AConnectionStringThatCarriesAPassword_IsRefused(string connectionString)
    {
        var configurator = new AzureRedisEntraAuthenticationConfigurator(new FakeTokenCredential(ObjectId));

        var act = () => configurator.Configure(ConfigurationOptions.Parse(connectionString));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AzureRedis:EntraAuthentication*")
            .WithMessage("*password*")
            .Which.Message.Should().NotContain("the-access-key", "the refusal never echoes the key");
    }

    [Theory]
    [InlineData("orders.redis.cache.windows.net:6380,ssl=false,abortConnect=false")]
    [InlineData("orders.redis.cache.windows.net:6379,ssl=False")]
    public void Configure_AConnectionStringThatDisablesTls_IsRefused(string connectionString)
    {
        var configurator = new AzureRedisEntraAuthenticationConfigurator(new FakeTokenCredential(ObjectId));

        var act = () => configurator.Configure(ConfigurationOptions.Parse(connectionString));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AzureRedis:EntraAuthentication*")
            .WithMessage("*ssl*");
    }

    [Fact]
    public void FrameworkClientPath_WithTheAzureConfigurator_RefusesAPasswordConnectionString()
    {
        // The refusal reaches a real framework connection point: resolving the Redis client's options
        // (what the client connects with) fails instead of handing out an access-key login.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = TlsConnectionString + ",password=the-access-key",
        });
        builder.Services.AddRedisConfigurationOptionsConfigurator(
            new AzureRedisEntraAuthenticationConfigurator(new FakeTokenCredential(ObjectId)));
        builder.AddRedisCaching();
        using var provider = builder.Services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<ConfigurationOptions>>().Value;

        act.Should().Throw<InvalidOperationException>().WithMessage("*password*");
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var noConfiguration = () => new ServiceCollection().AddAzureRedisEntraAuthentication(null!);
        var noCredential = () => new AzureRedisEntraAuthenticationConfigurator((TokenCredential)null!);
        var noOptions = () => new AzureRedisEntraAuthenticationConfigurator(new FakeTokenCredential(ObjectId)).Configure(null!);

        noConfiguration.Should().Throw<ArgumentNullException>();
        noCredential.Should().Throw<ArgumentNullException>();
        noOptions.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// A credential that hands out one fixed, unsigned JWT carrying an object id, and counts requests.
    /// Nothing leaves the process.
    /// </summary>
    private sealed class FakeTokenCredential : TokenCredential
    {
        private int _requests;

        public FakeTokenCredential(string objectId)
        {
            var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            Token = string.Join(
                '.',
                Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}"),
                Base64Url($"{{\"oid\":\"{objectId}\",\"exp\":{expires.ToString(CultureInfo.InvariantCulture)}}}"),
                Base64Url("signature"));
        }

        public string Token { get; }

        public int Requests => Volatile.Read(ref _requests);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return new AccessToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));

        private static string Base64Url(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
