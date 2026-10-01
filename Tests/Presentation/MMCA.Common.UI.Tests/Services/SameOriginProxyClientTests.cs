using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// The WebAssembly half of the same-origin API proxy (TD-08 Option A): with
/// <c>Api:SameOriginApiEndpoint</c> present the <c>"APIClient"</c> targets the proxy, never sends an
/// <c>Authorization</c> header and always sends <c>X-CSRF: 1</c>; the notification hub connects through
/// the proxy with no access-token provider; the bootstrap resolves the served relative path against
/// the page origin. Without the key every one of those stays exactly as it was.
/// </summary>
public sealed class SameOriginProxyClientTests
{
    private const string Gateway = "https://gateway.example.com/";
    private const string Proxy = "https://app.example.com/api/";

    [Fact]
    public async Task ApiClient_InProxyMode_TargetsTheProxy_StripsAuthorization_AndSendsCsrfHeader()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(sameOriginApiEndpoint: Proxy, capture, token: "claims-only-token");

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("APIClient");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "explicit-token");
        using var response = await client.GetAsync(new Uri("orders", UriKind.Relative), TestContext.Current.CancellationToken);

        capture.Request!.RequestUri.Should().Be(new Uri("https://app.example.com/api/orders"));
        capture.Request.Headers.Authorization.Should().BeNull("the proxy attaches the bearer server-side");
        capture.Request.Headers.GetValues(SameOriginProxyHeaders.CsrfHeaderName).Should().Equal(SameOriginProxyHeaders.CsrfHeaderValue);
    }

    [Fact]
    public async Task ApiClient_WithoutProxy_IsUnchanged_BearerAttached_NoCsrfHeader()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(sameOriginApiEndpoint: null, capture, token: "real-token");

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("APIClient");
        using var response = await client.GetAsync(new Uri("orders", UriKind.Relative), TestContext.Current.CancellationToken);

        capture.Request!.RequestUri.Should().Be(new Uri("https://gateway.example.com/orders"));
        capture.Request.Headers.Authorization!.Parameter.Should().Be("real-token");
        capture.Request.Headers.Contains(SameOriginProxyHeaders.CsrfHeaderName).Should().BeFalse();
    }

    [Fact]
    public void HubConnection_InProxyMode_UsesTheProxyUrl_WithNoTokenProvider_AndTheCsrfHeader()
    {
        var sut = CreateHub(new ApiSettings { ApiEndpoint = Gateway, SameOriginApiEndpoint = Proxy });
        var options = new HttpConnectionOptions();

        sut.ConfigureConnection(options);

        sut.UsesSameOriginProxy.Should().BeTrue();
        options.AccessTokenProvider.Should().BeNull("no client-held token may reach the hub connection");
        options.Headers[SameOriginProxyHeaders.CsrfHeaderName].Should().Be(SameOriginProxyHeaders.CsrfHeaderValue);
    }

    [Fact]
    public void HubConnection_WithoutProxy_KeepsTheTokenProvider()
    {
        var sut = CreateHub(new ApiSettings { ApiEndpoint = Gateway });
        var options = new HttpConnectionOptions();

        sut.ConfigureConnection(options);

        sut.UsesSameOriginProxy.Should().BeFalse();
        options.AccessTokenProvider.Should().NotBeNull();
        options.Headers.Should().NotContainKey(SameOriginProxyHeaders.CsrfHeaderName);
    }

    [Fact]
    public void Bootstrap_ResolvesTheRelativeProxyPathAgainstThePageOrigin()
    {
        var document = Encoding.UTF8.GetBytes("""{"api":{"apiEndpoint":"https://gateway.example.com","sameOriginApiEndpoint":"/api/"},"oauth":{"googleEnabled":true}}""");

        var resolved = MmcaClientConfigBootstrap.ResolveSameOriginApiEndpoint(document, new Uri("https://app.example.com/"));

        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(resolved)).Build();
        configuration["Api:SameOriginApiEndpoint"].Should().Be("https://app.example.com/api/");
        configuration["Api:ApiEndpoint"].Should().Be("https://gateway.example.com", "the gateway URL stays for full-page navigations");
        configuration["OAuth:GoogleEnabled"].Should().Be("True");
    }

    [Fact]
    public void Bootstrap_LeavesADocumentWithoutTheProxyKeyByteForByte()
    {
        var document = Encoding.UTF8.GetBytes("""{"api":{"apiEndpoint":"https://gateway.example.com"}}""");

        var resolved = MmcaClientConfigBootstrap.ResolveSameOriginApiEndpoint(document, new Uri("https://app.example.com/"));

        resolved.Should().BeSameAs(document);
    }

    private static NotificationHubService CreateHub(ApiSettings settings) =>
        new(new Mock<ITokenStorageService>().Object, Options.Create(settings), NullLogger<NotificationHubService>.Instance);

    private static ServiceProvider BuildProvider(string? sameOriginApiEndpoint, CapturingHandler capture, string token)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:ApiEndpoint"] = Gateway,
                ["Api:SameOriginApiEndpoint"] = sameOriginApiEndpoint,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ITokenStorageService>(new StubTokenStorageService { AccessToken = token });
        services.AddUIShared(configuration);
        services.AddHttpClient("APIClient").ConfigurePrimaryHttpMessageHandler(() => capture);
        return services.BuildServiceProvider();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
