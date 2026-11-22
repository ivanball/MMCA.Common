using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using MMCA.Common.API.SessionCookies;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>
/// A token-issuing path turns its answer into session cookies only when it opened a session (200 or
/// 201). A registration that awaits address confirmation answers 202 with no session: the proxy copies
/// that response through byte for byte, writes no session cookie, keeps a live view-as overlay, and
/// marks the response uncacheable.
/// </summary>
public sealed class SameOriginApiProxyRegistrationTests : IAsyncLifetime
{
    private const string PendingBody = """{"email":"new@example.com"}""";

    private FakeGateway _gateway = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Register202_IsCopiedThroughUnchanged_WithNoSessionCookie_AndNoStore()
    {
        _gateway.RegisterStatus = HttpStatusCode.Accepted;
        _gateway.RegisterBody = PendingBody;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = RegisterRequest();

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be(PendingBody, "a response that opened no session is passed through as the upstream sent it");
        AssertNoSessionCookie(response);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Pragma.Should().Contain(p => p.Name == "no-cache");
    }

    [Fact]
    public async Task Register202_CarryingTokenShapedFields_IsStillNotTurnedIntoCookies()
    {
        // Only the status says whether a session was opened; a 202 never did, whatever its body holds.
        var body = JsonSerializer.Serialize(new { accessToken = _gateway.LoginAccessToken, refreshToken = FakeGateway.LoginRefreshToken });
        _gateway.RegisterStatus = HttpStatusCode.Accepted;
        _gateway.RegisterBody = body;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = RegisterRequest();

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be(body);
        AssertNoSessionCookie(response);
    }

    [Fact]
    public async Task Register202_UnderALiveViewAsOverlay_KeepsTheOverlay()
    {
        var admin = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "admin");
        var viewAs = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "viewed-user");
        _gateway.RegisterStatus = HttpStatusCode.Accepted;
        _gateway.RegisterBody = PendingBody;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = RegisterRequest(admin, viewAs);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        ProxyHost.SetCookies(response).Should().NotContain(
            c => c.StartsWith(ViewAsOverlayCookie.Name, StringComparison.Ordinal),
            "nobody signed in, so the view in progress is not ended");
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    public async Task RegisterThatOpenedASession_StillMovesTheTokensIntoTheSessionCookies(HttpStatusCode status)
    {
        _gateway.RegisterStatus = status;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = RegisterRequest();

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(status);
        var cookies = ProxyHost.SetCookies(response);
        cookies.Should().Contain(c => c.StartsWith($"{SessionCookieEndpoints.RefreshTokenCookieName}={FakeGateway.LoginRefreshToken}", StringComparison.Ordinal));
        cookies.Should().Contain(c => c.StartsWith(SessionCookieEndpoints.AccessTokenCookieName + "=", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("refreshToken").GetString().Should().BeEmpty("the refresh token never reaches script");
    }

    [Fact]
    public async Task Register201_UnderALiveViewAsOverlay_EndsTheOverlay()
    {
        var admin = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "admin");
        var viewAs = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "viewed-user");
        _gateway.RegisterStatus = HttpStatusCode.Created;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = RegisterRequest(admin, viewAs);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        ProxyHost.SetCookies(response).Should().Contain(
            c => c.StartsWith(ViewAsOverlayCookie.Name + "=;", StringComparison.Ordinal),
            "a new session replaces the one the view was taken under");
    }

    private static HttpRequestMessage RegisterRequest(string? accessToken = null, string? viewAsToken = null)
    {
        var request = ProxyHost.Request(HttpMethod.Post, "/api/auth/register", accessToken, accessToken is null ? null : "refresh-1", csrf: true, viewAsToken: viewAsToken);
        request.Content = new StringContent(
            """{"email":"new@example.com","password":"Password123!","firstName":"Ada","lastName":"Lovelace"}""",
            System.Text.Encoding.UTF8,
            "application/json");
        return request;
    }

    private static void AssertNoSessionCookie(HttpResponseMessage response) =>
        ProxyHost.SetCookies(response).Should().NotContain(c =>
            c.StartsWith(SessionCookieEndpoints.AccessTokenCookieName, StringComparison.Ordinal)
            || c.StartsWith(SessionCookieEndpoints.RefreshTokenCookieName, StringComparison.Ordinal));
}
