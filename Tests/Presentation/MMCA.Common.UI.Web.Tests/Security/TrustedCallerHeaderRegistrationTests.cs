using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.UI.Web.Tests.Security;

/// <summary>
/// Pins <c>AddTrustedCallerHeader(IConfiguration)</c>: it is opt in (nothing at all is registered
/// without a secret), it reads the same <c>GatewayRateLimiting</c> section the gateway edge limiter
/// reads, it defaults the header name from that settings type, and it applies to EVERY named client
/// the host creates (the cookie-session refresh client is created under a name a host cannot reach).
/// </summary>
public sealed class TrustedCallerHeaderRegistrationTests
{
    private const string DefaultHeaderName = "X-Internal-Caller-Key";
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private const string GatewayOrigin = "https://gateway.example.com:6001";

    [Fact]
    public void AddTrustedCallerHeader_WithNoSecret_RegistersNothing()
    {
        var services = new ServiceCollection();
        var before = services.Count;

        services.AddTrustedCallerHeader(BuildConfiguration(secret: null));

        services.Count.Should().Be(before);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddTrustedCallerHeader_WithNoSecret_SendsNoHeader(string? secret)
    {
        var (request, _) = await SendThroughNamedClientAsync(
            BuildConfiguration(secret), "AnyClient", GatewayOrigin + "/Auth/refresh");

        request.Headers.Contains(DefaultHeaderName).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-absolute-uri")]
    public void AddTrustedCallerHeader_WithoutAnAbsoluteGatewayOrigin_RegistersNothing(string? apiEndpoint)
    {
        var services = new ServiceCollection();
        var before = services.Count;

        services.AddTrustedCallerHeader(BuildConfiguration(Secret, apiEndpoint));

        services.Count.Should().Be(before);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddTrustedCallerHeader_WithABlankHeaderName_RegistersNothing(string headerName)
    {
        var services = new ServiceCollection();
        var before = services.Count;

        services.AddTrustedCallerHeader(BuildConfiguration(Secret, GatewayOrigin, headerName));

        services.Count.Should().Be(before);
    }

    [Fact]
    public async Task AddTrustedCallerHeader_WithASecret_StampsRequestsToTheGatewayOrigin()
    {
        var (request, _) = await SendThroughNamedClientAsync(
            BuildConfiguration(Secret), "AnyClient", GatewayOrigin + "/Auth/refresh");

        request.Headers.GetValues(DefaultHeaderName).Should().ContainSingle().Which.Should().Be(Secret);
    }

    [Theory]
    [InlineData("https://telemetry.example.com/ingest")]
    [InlineData("https://gateway.example.com:6002/Auth/refresh")]
    public async Task AddTrustedCallerHeader_WithASecret_StampsNothingBoundElsewhere(string requestUri)
    {
        var (request, _) = await SendThroughNamedClientAsync(
            BuildConfiguration(Secret), "ThirdPartyClient", requestUri);

        request.Headers.Contains(DefaultHeaderName).Should().BeFalse();
    }

    [Theory]
    [InlineData("SessionCookieRefreshClient")]
    [InlineData("SomeAppSpecificClient")]
    [InlineData("")]
    public async Task AddTrustedCallerHeader_AppliesToEveryNamedClient(string clientName)
    {
        var (request, _) = await SendThroughNamedClientAsync(
            BuildConfiguration(Secret), clientName, GatewayOrigin + "/Events");

        request.Headers.Contains(DefaultHeaderName).Should().BeTrue();
    }

    [Fact]
    public async Task AddTrustedCallerHeader_WithOnlyASecret_UsesTheGatewaySettingsDefaultHeaderName()
    {
        var (request, _) = await SendThroughNamedClientAsync(
            BuildConfiguration(Secret), "AnyClient", GatewayOrigin + "/Events");

        request.Headers.Contains(DefaultHeaderName).Should().BeTrue();
    }

    [Fact]
    public async Task AddTrustedCallerHeader_WithAConfiguredHeaderName_UsesIt()
    {
        var configuration = BuildConfiguration(Secret, GatewayOrigin, headerName: "X-Deployment-Caller");

        var (request, _) = await SendThroughNamedClientAsync(configuration, "AnyClient", GatewayOrigin + "/Events");

        request.Headers.Contains(DefaultHeaderName).Should().BeFalse();
        request.Headers.GetValues("X-Deployment-Caller").Should().ContainSingle().Which.Should().Be(Secret);
    }

    private static IConfiguration BuildConfiguration(
        string? secret,
        string? apiEndpoint = GatewayOrigin,
        string? headerName = null)
    {
        // A null argument means the key is ABSENT, the way a real appsettings file omits it: an
        // in-memory entry whose value is null is a present-but-null key, which binds over the
        // settings type's own default rather than leaving it in place.
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (apiEndpoint is not null)
        {
            values["Api:ApiEndpoint"] = apiEndpoint;
        }

        if (secret is not null)
        {
            values["GatewayRateLimiting:TrustedCallerSecret"] = secret;
        }

        if (headerName is not null)
        {
            values["GatewayRateLimiting:TrustedCallerHeaderName"] = headerName;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // Registers the handler through the extension, resolves the named client from the factory (so
    // the ConfigureAll breadth is what is under test, not a hand-composed pipeline), and returns the
    // request as it left the pipeline.
    private static async Task<(HttpRequestMessage Request, HttpResponseMessage Response)> SendThroughNamedClientAsync(
        IConfiguration configuration,
        string clientName,
        string requestUri)
    {
        var inner = new CapturingHandler();
        var services = new ServiceCollection();
        services.AddTrustedCallerHeader(configuration);
        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => inner);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        var response = await client.GetAsync(new Uri(requestUri), TestContext.Current.CancellationToken);

        inner.LastRequest.Should().NotBeNull();
        return (inner.LastRequest, response);
    }

    /// <summary>Stub primary handler that records the request the client pipeline produced.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        }
    }
}
