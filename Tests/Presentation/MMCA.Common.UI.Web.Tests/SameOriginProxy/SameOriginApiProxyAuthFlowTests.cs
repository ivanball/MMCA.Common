using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.UI.Web.SameOriginProxy;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>
/// The sign-in, refresh and sign-out flows through the proxy, plus the Blazor Server circuit's
/// handoff endpoints: a token pair from the gateway lands only in the HttpOnly cookies and the browser
/// gets a body with the credentials stripped; a refresh is answered from the refresh cookie; a
/// sign-out forwards the bearer and clears the cookies; the circuit trades only protected handoffs.
/// </summary>
public sealed class SameOriginApiProxyAuthFlowTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Login_MovesTheTokenPairIntoStrictHttpOnlyCookies_AndStripsItFromTheBody()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/api/auth/login", csrf: true);
        request.Content = JsonContent.Create(new LoginRequest("ada@example.com", "secret"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(_gateway.LoginAccessToken).And.NotContain(FakeGateway.LoginRefreshToken);

        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("refreshToken").GetString().Should().BeEmpty();
        var claimsOnly = new JwtSecurityTokenHandler().ReadJwtToken(json.RootElement.GetProperty("accessToken").GetString());
        claimsOnly.Header.Alg.Should().Be("none", "the browser only gets an unsigned claims copy");
        claimsOnly.Subject.Should().Be("login-user");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        var cookies = ProxyHost.SetCookies(response);
        cookies.Should().Contain(c => c.StartsWith($"{SessionCookieEndpoints.AccessTokenCookieName}={_gateway.LoginAccessToken}", StringComparison.Ordinal));
        cookies.Should().Contain(c => c.StartsWith($"{SessionCookieEndpoints.RefreshTokenCookieName}={FakeGateway.LoginRefreshToken}", StringComparison.Ordinal));
        cookies.Should().OnlyContain(c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Refresh_IsAnsweredByTheProxy_FromTheRefreshCookie()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/api/auth/refresh", Jwt.Create(DateTime.UtcNow.AddMinutes(10)), "refresh-1", csrf: true);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _gateway.RefreshCalls.Should().Be(1);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(_gateway.RotatedAccessToken).And.NotContain(FakeGateway.RotatedRefreshToken);
        ProxyHost.SetCookies(response).Should().Contain(c => c.Contains(FakeGateway.RotatedRefreshToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Revoke_ForwardsTheBearer_ThenClearsTheCookies()
    {
        var access = Jwt.Create(DateTime.UtcNow.AddMinutes(10));
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/api/auth/revoke", access, "refresh-1", csrf: true);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _gateway.Seen.Should().ContainSingle().Which.Authorization.Should().Be($"Bearer {access}");
        ProxyHost.SetCookies(response).Should().HaveCount(2).And.OnlyContain(c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SessionTokenEndpoint_HandsScriptAClaimsCopy_NeverTheAccessToken()
    {
        var access = Jwt.Create(DateTime.UtcNow.AddMinutes(10));
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/auth/session/token", access, "refresh-1");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<SessionTokenResponse>(TestContext.Current.CancellationToken);
        payload!.AccessToken.Should().NotBe(access);
        new JwtSecurityTokenHandler().ReadJwtToken(payload.AccessToken).Header.Alg.Should().Be("none");
    }

    [Fact]
    public async Task TokenHandoff_ReturnsCiphertextOnlyTheServerCanOpen()
    {
        var access = Jwt.Create(DateTime.UtcNow.AddMinutes(10));
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/auth/session/handoff", access, "refresh-1", csrf: true);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(access);
        var handoff = JsonDocument.Parse(body).RootElement.GetProperty("handoff").GetString();
        var protector = host.Services.GetRequiredService<SessionHandoffProtector>();
        protector.UnprotectAccessToken(handoff).Should().Be(access);
        protector.UnprotectTokenPair(handoff).Should().BeNull("an access-token handoff cannot be replayed as a cookie write");
    }

    [Fact]
    public async Task TokenHandoff_WithoutTheCsrfHeader_Is403()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/auth/session/handoff", Jwt.Create(DateTime.UtcNow.AddMinutes(10)), "refresh-1");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CookieHandoff_WritesTheCookiesFromAProtectedPair_AndRefusesAnythingElse()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        var protector = host.Services.GetRequiredService<SessionHandoffProtector>();
        var access = Jwt.Create(DateTime.UtcNow.AddMinutes(10));

        using var good = ProxyHost.Request(HttpMethod.Post, "/auth/session-cookie/handoff", csrf: true);
        good.Content = JsonContent.Create(new { handoff = protector.ProtectTokenPair(access, "refresh-9") });
        using var goodResponse = await client.SendAsync(good, TestContext.Current.CancellationToken);

        using var forged = ProxyHost.Request(HttpMethod.Post, "/auth/session-cookie/handoff", csrf: true);
        forged.Content = JsonContent.Create(new { handoff = protector.ProtectAccessToken(access) });
        using var forgedResponse = await client.SendAsync(forged, TestContext.Current.CancellationToken);

        goodResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        ProxyHost.SetCookies(goodResponse).Should().Contain(c => c.StartsWith($"{SessionCookieEndpoints.RefreshTokenCookieName}=refresh-9", StringComparison.Ordinal));
        forgedResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ProxyHost.SetCookies(forgedResponse).Should().BeEmpty();
    }
}
