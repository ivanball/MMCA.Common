using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using MMCA.Common.API.SessionCookies;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>
/// The view-as overlay cookie on the same-origin proxy (T20, ADR-137): the grant response's token
/// moves into a separate HttpOnly cookie while the staff user's own session cookies are untouched,
/// the overlay's token replaces the session bearer while it is live, and ending the view, signing
/// out, an expired overlay and an upstream 401 all drop it.
/// </summary>
public sealed class SameOriginApiProxyViewAsTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task GrantResponse_MovesTheTokenIntoTheOverlayCookie_LeavesTheSessionAlone_AndStripsTheBody()
    {
        var admin = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "admin");
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/api/Admin/Users/7/view-as", admin, "refresh-1", csrf: true);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _gateway.Seen.Should().ContainSingle().Which.Authorization.Should().Be($"Bearer {admin}", "the grant is asked for with the staff user's own session");
        var cookies = ProxyHost.SetCookies(response);
        var overlay = cookies.Should().ContainSingle(c => c.StartsWith(ViewAsOverlayCookie.Name + "=", StringComparison.Ordinal)).Subject;
        overlay.Should().Contain(_gateway.ViewAsAccessToken);
        overlay.Should().ContainEquivalentOf("httponly").And.ContainEquivalentOf("samesite=strict").And.ContainEquivalentOf("max-age");
        cookies.Should().NotContain(c => c.StartsWith(SessionCookieEndpoints.AccessTokenCookieName, StringComparison.Ordinal)
            || c.StartsWith(SessionCookieEndpoints.RefreshTokenCookieName, StringComparison.Ordinal));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(_gateway.ViewAsAccessToken, "the signed token never reaches script");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty("the browser still gets the claims-only copy");
    }

    // Review fix: only a 2xx answer whose token parses AND carries act_sub and vag writes the overlay.
    [Fact]
    public async Task AnUnrelatedViewAsPath_WhoseTokenIsNotAViewAsToken_PassesThroughUntouched_AndWritesNoCookie()
    {
        var admin = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "admin");
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/api/reports/9/view-as", admin, "refresh-1", csrf: true);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ProxyHost.SetCookies(response).Should().NotContain(c => c.StartsWith(ViewAsOverlayCookie.Name, StringComparison.Ordinal));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("accessToken").GetString().Should().Be(_gateway.UnrelatedAccessToken, "the body is passed through as the upstream sent it");
        json.RootElement.GetProperty("title").GetString().Should().Be("Quarterly view-as report");
    }

    [Fact]
    public async Task ARefusedGrant_WritesNoCookie()
    {
        var admin = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "admin");
        _gateway.RejectedTokens[admin] = true;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/api/Admin/Users/7/view-as", admin, "refresh-1", csrf: true);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ProxyHost.SetCookies(response).Should().NotContain(c => c.StartsWith(ViewAsOverlayCookie.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithALiveOverlay_TheOverlayTokenReplacesTheSessionBearer()
    {
        var admin = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "admin");
        var viewAs = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "viewed-user");
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, "/api/orders", admin, "refresh-1", viewAsToken: viewAs);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var seen = _gateway.Seen.Should().ContainSingle().Subject;
        seen.Authorization.Should().Be($"Bearer {viewAs}");
        seen.Cookie.Should().Be("theme=dark", "the overlay never travels upstream as a cookie either");
    }

    [Fact]
    public async Task EndViewAs_ForwardsTheOverlayBearer_ThenDeletesTheOverlay_AndKeepsTheSession()
    {
        var admin = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "admin");
        var viewAs = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "viewed-user");
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/api/auth/view-as/end", admin, "refresh-1", csrf: true, viewAsToken: viewAs);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _gateway.Seen.Should().ContainSingle().Which.Authorization.Should().Be($"Bearer {viewAs}");
        var cookies = ProxyHost.SetCookies(response);
        cookies.Should().ContainSingle(c => c.StartsWith(ViewAsOverlayCookie.Name + "=;", StringComparison.Ordinal), "the overlay is expired");
        cookies.Should().NotContain(c => c.StartsWith(SessionCookieEndpoints.AccessTokenCookieName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignOut_UnderTheOverlay_UsesTheSessionBearer_AndClearsBoth()
    {
        var admin = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "admin");
        var viewAs = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "viewed-user");
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/api/auth/revoke", admin, "refresh-1", csrf: true, viewAsToken: viewAs);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        _gateway.Seen.Should().ContainSingle().Which.Authorization.Should().Be($"Bearer {admin}");
        var cookies = ProxyHost.SetCookies(response);
        cookies.Should().Contain(c => c.StartsWith(ViewAsOverlayCookie.Name + "=;", StringComparison.Ordinal));
        cookies.Should().Contain(c => c.StartsWith(SessionCookieEndpoints.AccessTokenCookieName + "=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpiredOverlay_IsIgnoredAndDeleted_AndTheSessionBearerIsUsed()
    {
        var admin = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "admin");
        var expired = Jwt.Create(DateTime.UtcNow.AddMinutes(-1), "viewed-user");
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, "/api/orders", admin, "refresh-1", viewAsToken: expired);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        _gateway.Seen.Should().ContainSingle().Which.Authorization.Should().Be($"Bearer {admin}");
        ProxyHost.SetCookies(response).Should().Contain(c => c.StartsWith(ViewAsOverlayCookie.Name + "=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpstreamRefusingTheOverlay_DeletesIt_WithoutRefreshingOrReplaying()
    {
        var admin = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "admin");
        var viewAs = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "viewed-user");
        _gateway.RejectedTokens[viewAs] = true;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, "/api/orders", admin, "refresh-1", viewAsToken: viewAs);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _gateway.RefreshCalls.Should().Be(0, "a view-as token is never renewed");
        _gateway.Seen.Should().ContainSingle();
        ProxyHost.SetCookies(response).Should().Contain(c => c.StartsWith(ViewAsOverlayCookie.Name + "=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionTokenEndpoint_PrefersALiveOverlay_SoTheBrowserSeesTheViewedUser()
    {
        var admin = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "admin");
        var viewAs = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "viewed-user");
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/auth/session/token", admin, "refresh-1", viewAsToken: viewAs);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SessionTokenResponse>(TestContext.Current.CancellationToken);
        var claims = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(body!.AccessToken).Claims;
        claims.Should().Contain(c => c.Type == "sub" && c.Value == "viewed-user");
    }
}
