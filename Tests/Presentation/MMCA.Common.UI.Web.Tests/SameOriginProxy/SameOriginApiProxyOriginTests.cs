using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using MMCA.Common.API.Startup;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>
/// The proxy serves only same-origin browser traffic: a foreign <c>Origin</c> (a same-site sibling
/// included) or a <c>Sec-Fetch-Site</c> other than <c>same-origin</c> is refused 403 before anything
/// reaches the gateway, a WebSocket upgrade needs this host's own <c>Origin</c>, and <c>OPTIONS</c> is
/// answered locally with no CORS grant, so the gateway's CORS policy is never consulted through it.
/// </summary>
public sealed class SameOriginApiProxyOriginTests : IAsyncLifetime
{
    private const string OwnOrigin = "https://app.example.com";
    private const string OwnUrl = OwnOrigin + "/api/orders";

    private FakeGateway _gateway = null!;

    public static TheoryData<string> ForeignOrigins =>
        [
            "https://evil.example",
            "https://other.example.com",
            "https://app.example.com.evil.example",
            "http://app.example.com",
            "https://app.example.com:8443",
            "null",
            "https://app.example.com/path",
        ];

    public static TheoryData<string> CrossSiteFetchSites => ["cross-site", "same-site"];

    public static TheoryData<string> AllowedFetchSites => ["same-origin", "none"];

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Theory]
    [MemberData(nameof(ForeignOrigins))]
    public async Task Get_WithAForeignOrigin_Is403_AndNeverForwarded(string origin)
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, OwnUrl, Jwt.Create(DateTime.UtcNow.AddMinutes(10)), "refresh-1");
        request.Headers.Add("Origin", origin);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("cross_origin_rejected");
        _gateway.Seen.Should().BeEmpty();
        _gateway.RefreshCalls.Should().Be(0);
    }

    [Fact]
    public async Task Post_WithASameSiteSiblingOrigin_Is403_EvenWithTheCsrfHeader()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, OwnUrl, Jwt.Create(DateTime.UtcNow.AddMinutes(10)), "refresh-1", csrf: true);
        request.Headers.Add("Origin", "https://evil.example.com");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _gateway.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Post_WithTheOwnOrigin_IsForwarded()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, OwnUrl, Jwt.Create(DateTime.UtcNow.AddMinutes(10)), "refresh-1", csrf: true);
        request.Headers.Add("Origin", OwnOrigin);
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _gateway.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task Post_WithTheOwnOriginButAnExplicitDefaultPort_IsForwarded()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, OwnUrl, csrf: true);
        request.Headers.Add("Origin", OwnOrigin + ":443");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BehindAReverseProxy_TheOriginIsComparedWithTheForwardedSchemeAndHost()
    {
        using var host = await ProxyHost.StartAsync(_gateway, beforeRouting: app => app.UseCommonUiForwardedHeaders());
        using var client = host.GetTestClient();

        // The app listens on plain http behind the ingress; the browser saw https://public.example.com.
        using var request = ProxyHost.Request(HttpMethod.Post, "http://internal-ui:8080/api/orders", csrf: true);
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "public.example.com");
        request.Headers.Add("Origin", "https://public.example.com");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _gateway.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task Upgrade_WithAForeignOrigin_Is403_AndNeverForwarded()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = UpgradeRequest(Jwt.Create(DateTime.UtcNow.AddMinutes(10)));
        request.Headers.Add("Origin", "https://evil.example.com");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a WebSocket is not CORS-protected, so the origin is the only gate");
        _gateway.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Upgrade_WithoutAnOrigin_Is403_AndNeverForwarded()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = UpgradeRequest(Jwt.Create(DateTime.UtcNow.AddMinutes(10)));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a browser always sends Origin on an upgrade");
        _gateway.Seen.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(CrossSiteFetchSites))]
    public async Task Get_WithACrossOriginFetchSite_Is403_AndNeverForwarded(string fetchSite)
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, OwnUrl, Jwt.Create(DateTime.UtcNow.AddMinutes(10)), "refresh-1");
        request.Headers.Add("Sec-Fetch-Site", fetchSite);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _gateway.Seen.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllowedFetchSites))]
    public async Task Get_WithASameOriginOrUserInitiatedFetchSite_IsForwarded(string fetchSite)
    {
        var access = Jwt.Create(DateTime.UtcNow.AddMinutes(10));
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, OwnUrl, access, "refresh-1");
        request.Headers.Add("Sec-Fetch-Site", fetchSite);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _gateway.Seen.Should().ContainSingle().Which.Authorization.Should().Be($"Bearer {access}");
    }

    [Fact]
    public async Task Post_WithAUserInitiatedFetchSite_Is403()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, OwnUrl, csrf: true);
        request.Headers.Add("Sec-Fetch-Site", "none");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "only a navigation (GET/HEAD) is user-initiated");
        _gateway.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task CrossOriginPreflight_Is403_WithNoCorsGrant_AndNeverReachesTheGateway()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, OwnUrl);
        request.Headers.Add("Origin", "https://evil.example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-csrf");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        response.Headers.Contains("Access-Control-Allow-Headers").Should().BeFalse();
        _gateway.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task SameOriginOptions_IsAnsweredLocally_With204_AndNoCorsGrant()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Options, OwnUrl, Jwt.Create(DateTime.UtcNow.AddMinutes(10)), "refresh-1");
        request.Headers.Add("Origin", OwnOrigin);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        _gateway.Seen.Should().BeEmpty("OPTIONS never reaches the gateway");
    }

    [Fact]
    public async Task OptionsWithoutAnOrigin_IsAnsweredLocally_AndNeverReachesTheGateway()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, OwnUrl);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _gateway.Seen.Should().BeEmpty();
    }

    private static HttpRequestMessage UpgradeRequest(string accessToken)
    {
        var request = ProxyHost.Request(HttpMethod.Get, OwnOrigin + "/api/hubs/notifications", accessToken, "refresh-1");
        request.Headers.Connection.Add("Upgrade");
        request.Headers.Upgrade.Add(new System.Net.Http.Headers.ProductHeaderValue("websocket"));
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        return request;
    }
}
