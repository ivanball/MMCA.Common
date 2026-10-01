using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using MMCA.Common.API.SessionCookies;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>
/// The proxy's token handling end to end, browser to proxy to an in-process gateway: the bearer is
/// attached server-side from the HttpOnly cookie (never from the browser), an expiring access token
/// is refreshed once per session however many requests race on it, an upstream 401 earns one
/// refresh-and-replay for a safe method and none for a POST, and a session that cannot refresh is
/// cleared and answered 401.
/// </summary>
public sealed class SameOriginApiProxyTokenTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Get_WithSession_AttachesTheCookieTokenAsBearer_AndKeepsTokensOffTheUpstream()
    {
        var access = Jwt.Create(DateTime.UtcNow.AddMinutes(10));
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, "/api/orders/5?page=2", access, "refresh-1");
        request.Headers.Add("Authorization", "Bearer smuggled-by-script");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var seen = _gateway.Seen.Should().ContainSingle().Subject;
        seen.PathAndQuery.Should().Be("/orders/5?page=2", "the proxy prefix is removed");
        seen.Authorization.Should().Be($"Bearer {access}", "the bearer comes from the cookie, not from the browser");
        seen.Cookie.Should().Be("theme=dark", "the session cookies never travel upstream");
    }

    [Fact]
    public async Task Get_WithoutSession_ForwardsAnonymously()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, "/api/catalog");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _gateway.Seen.Should().ContainSingle().Which.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task Get_ExpiredAccessToken_RefreshesServerSide_ReissuesTheCookies_AndForwardsTheNewBearer()
    {
        var expired = Jwt.Create(DateTime.UtcNow.AddMinutes(-5));
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, "/api/orders", expired, "refresh-1");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _gateway.RefreshCalls.Should().Be(1);
        _gateway.Seen.Should().ContainSingle().Which.Authorization.Should().Be($"Bearer {_gateway.RotatedAccessToken}");
        var cookies = ProxyHost.SetCookies(response);
        cookies.Should().Contain(c => c.StartsWith($"{SessionCookieEndpoints.RefreshTokenCookieName}={FakeGateway.RotatedRefreshToken}", StringComparison.Ordinal));
        cookies.Should().OnlyContain(c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConcurrentRequests_OnOneExpiredSession_ShareASingleRefresh()
    {
        var expired = Jwt.Create(DateTime.UtcNow.AddMinutes(-5));
        _gateway.RefreshDelay = TimeSpan.FromMilliseconds(200);
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var request = ProxyHost.Request(HttpMethod.Get, "/api/orders/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), expired, "refresh-1");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            return response.StatusCode;
        }));

        responses.Should().OnlyContain(status => status == HttpStatusCode.OK);
        _gateway.RefreshCalls.Should().Be(1, "concurrent requests of one session must not rotate the refresh token twice");
        _gateway.Seen.Should().HaveCount(8).And.OnlyContain(s => s.Authorization == $"Bearer {_gateway.RotatedAccessToken}");
    }

    [Fact]
    public async Task Get_Upstream401_RefreshesOnce_AndReplaysWithTheNewBearer()
    {
        var revoked = Jwt.Create(DateTime.UtcNow.AddMinutes(10));
        _gateway.RejectedTokens[revoked] = true;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, "/api/orders", revoked, "refresh-1");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _gateway.RefreshCalls.Should().Be(1);
        _gateway.Seen.Select(s => s.Authorization).Should().Equal($"Bearer {revoked}", $"Bearer {_gateway.RotatedAccessToken}");
    }

    [Fact]
    public async Task Post_Upstream401_IsNotReplayed()
    {
        var revoked = Jwt.Create(DateTime.UtcNow.AddMinutes(10));
        _gateway.RejectedTokens[revoked] = true;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/api/orders", revoked, "refresh-1", csrf: true);
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a non-idempotent request is never re-sent");
        _gateway.RefreshCalls.Should().Be(0);
        _gateway.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task Get_Upstream401_AndTheRefreshFails_Returns401_AndClearsTheCookies()
    {
        var revoked = Jwt.Create(DateTime.UtcNow.AddMinutes(10));
        _gateway.RejectedTokens[revoked] = true;
        _gateway.FailRefresh = true;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, "/api/orders", revoked, "refresh-1");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ProxyHost.SetCookies(response).Should().HaveCount(2).And.OnlyContain(c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExpiredRefreshToken_Returns401_AndClearsTheCookies_WithoutReachingTheUpstream()
    {
        var expired = Jwt.Create(DateTime.UtcNow.AddMinutes(-5));
        _gateway.FailRefresh = true;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, "/api/orders", expired, "expired-refresh");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("session_expired");
        ProxyHost.SetCookies(response).Should().HaveCount(2).And.OnlyContain(c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        _gateway.Seen.Should().BeEmpty();
    }
}
