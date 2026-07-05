using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API.SessionCookies;

namespace MMCA.Common.API.Tests.SessionCookies;

/// <summary>
/// In-memory TestServer smoke tests over the mapped session-cookie routes:
/// <c>POST/DELETE /auth/session-cookie</c> seed and clear the HttpOnly cookies, and
/// <c>POST /auth/session/token</c> enforces the Sec-Fetch-Site cross-site guard, returns 401
/// JSON with no session, and never serializes the refresh token to the browser.
/// </summary>
public sealed class SessionCookieEndpointsTests
{
    // ── POST /auth/session-cookie ──
    [Fact]
    public async Task PostSessionCookie_SeedsBothHttpOnlyCookies_AndReturns204()
    {
        using var host = await CreateHostAsync(new StubRefresher(result: null));
        using HttpClient client = host.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/auth/session-cookie",
            new SessionCookieEndpoints.SessionCookieRequest("the-access", "the-refresh"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var setCookies = GetSetCookies(response);
        setCookies.Should().HaveCount(2);
        setCookies.Should().ContainSingle(c => c.StartsWith($"{SessionCookieEndpoints.AccessTokenCookieName}=the-access", StringComparison.Ordinal));
        setCookies.Should().ContainSingle(c => c.StartsWith($"{SessionCookieEndpoints.RefreshTokenCookieName}=the-refresh", StringComparison.Ordinal));
        setCookies.Should().OnlyContain(c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    // ── DELETE /auth/session-cookie ──
    [Fact]
    public async Task DeleteSessionCookie_ExpiresBothCookies_AndReturns204()
    {
        using var host = await CreateHostAsync(new StubRefresher(result: null));
        using HttpClient client = host.GetTestClient();

        using var response = await client.DeleteAsync(new Uri("/auth/session-cookie", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var setCookies = GetSetCookies(response);
        setCookies.Should().HaveCount(2);
        setCookies.Should().OnlyContain(
            c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase),
            "deletion is expressed as an epoch expiry");
    }

    // ── POST /auth/session/token ──
    [Fact]
    public async Task PostSessionToken_CrossSiteRequest_Returns403()
    {
        using var host = await CreateHostAsync(new StubRefresher(
            new SessionTokenResult("valid-access", DateTime.UtcNow.AddMinutes(10))));
        using HttpClient client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/session/token");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a browser-flagged cross-site POST must be rejected even when a session exists");
    }

    [Fact]
    public async Task PostSessionToken_SameOriginRequest_IsNotBlockedByCrossSiteGuard()
    {
        using var host = await CreateHostAsync(new StubRefresher(result: null));
        using HttpClient client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/session/token");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "same-origin passes the guard and falls through to the no-session 401");
    }

    [Fact]
    public async Task PostSessionToken_NoSession_Returns401WithErrorJson()
    {
        using var host = await CreateHostAsync(new StubRefresher(result: null));
        using HttpClient client = host.GetTestClient();

        using var response = await client.PostAsync(new Uri("/auth/session/token", UriKind.Relative), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("no_session");
    }

    [Fact]
    public async Task PostSessionToken_ValidSession_ReturnsAccessTokenButNeverTheRefreshToken()
    {
        var expiry = new DateTime(2026, 7, 5, 12, 30, 0, DateTimeKind.Utc);
        using var host = await CreateHostAsync(new StubRefresher(new SessionTokenResult("the-access-token", expiry)));
        using HttpClient client = host.GetTestClient();

        using var response = await client.PostAsync(new Uri("/auth/session/token", UriKind.Relative), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SessionTokenResponse? payload = await response.Content.ReadFromJsonAsync<SessionTokenResponse>();
        payload.Should().NotBeNull();
        payload!.AccessToken.Should().Be("the-access-token");
        payload.AccessTokenExpiry.Should().Be(expiry);

        string raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContainAny(
            ["refreshToken", "RefreshToken", "refresh_token"],
            "the refresh token must live only in the HttpOnly cookie, never in a browser-readable body");
    }

    // ── Helpers ──
    private static async Task<IHost> CreateHostAsync(ICookieSessionRefresher refresher) =>
        await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .UseEnvironment(Environments.Production)
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(refresher);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapSessionCookieEndpoints());
                }))
            .StartAsync();

    private static List<string> GetSetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) ? [.. values] : [];

    private sealed class StubRefresher(SessionTokenResult? result) : ICookieSessionRefresher
    {
        public Task<SessionTokenResult?> GetOrRefreshAsync(
            HttpContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
