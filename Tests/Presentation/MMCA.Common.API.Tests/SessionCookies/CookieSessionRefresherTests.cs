using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.Shared.Auth;
using Moq;

namespace MMCA.Common.API.Tests.SessionCookies;

/// <summary>
/// Verifies <see cref="CookieSessionRefresher"/>: a still-valid access cookie is returned
/// without any HTTP call, an expired one is exchanged server-to-server at <c>auth/refresh</c>
/// (rotated cookies written back, fresh token stashed on Items), failures return null, and the
/// rotation-grace cache collapses sibling refreshes carrying the same old refresh token.
/// </summary>
public sealed class CookieSessionRefresherTests
{
    // ── Valid access cookie: no refresh ──
    [Fact]
    public async Task GetOrRefreshAsync_WhenAccessTokenStillValid_ReturnsItWithoutHttpCall()
    {
        DateTime expires = DateTime.UtcNow.AddMinutes(10);
        string token = CreateJwt(expires);
        using var harness = CreateSut(RespondWith(HttpStatusCode.InternalServerError));
        var context = CreateContext(accessToken: token, refreshToken: "refresh-1");

        SessionTokenResult? result = await harness.Sut.GetOrRefreshAsync(context);

        result.Should().NotBeNull();
        result!.Value.AccessToken.Should().Be(token);
        result.Value.AccessTokenExpiry.Should().BeCloseTo(expires, TimeSpan.FromSeconds(2));
        harness.Handler.CallCount.Should().Be(0, "a valid token must never trigger a refresh round-trip");
        context.Response.Headers.SetCookie.Count.Should().Be(0);
    }

    [Fact]
    public async Task GetOrRefreshAsync_WhenTokenExpiresWithinClockSkew_TreatsItAsExpired()
    {
        // Valid for 10 more seconds, but the 30-second skew makes it count as expired.
        // With no refresh cookie the session is gone.
        string token = CreateJwt(DateTime.UtcNow.AddSeconds(10));
        using var harness = CreateSut(RespondWith(HttpStatusCode.InternalServerError));
        var context = CreateContext(accessToken: token, refreshToken: null);

        SessionTokenResult? result = await harness.Sut.GetOrRefreshAsync(context);

        result.Should().BeNull();
    }

    // ── No session ──
    [Fact]
    public async Task GetOrRefreshAsync_WhenNoCookiesAtAll_ReturnsNullWithoutHttpCall()
    {
        using var harness = CreateSut(RespondWith(HttpStatusCode.OK));
        var context = new DefaultHttpContext();

        SessionTokenResult? result = await harness.Sut.GetOrRefreshAsync(context);

        result.Should().BeNull();
        harness.Handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetOrRefreshAsync_NullContext_ThrowsArgumentNullException()
    {
        using var harness = CreateSut(RespondWith(HttpStatusCode.OK));

        Func<Task> act = () => harness.Sut.GetOrRefreshAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Refresh flow ──
    [Fact]
    public async Task GetOrRefreshAsync_WhenAccessExpiredAndRefreshPresent_RotatesViaAuthRefresh()
    {
        string expired = CreateJwt(DateTime.UtcNow.AddMinutes(-5));
        DateTime newExpiry = DateTime.UtcNow.AddMinutes(15);
        using var harness = CreateSut(RespondWithTokens("new-access", "new-refresh", newExpiry));
        var context = CreateContext(accessToken: expired, refreshToken: "old-refresh");

        SessionTokenResult? result = await harness.Sut.GetOrRefreshAsync(context);

        result.Should().NotBeNull();
        result!.Value.AccessToken.Should().Be("new-access");
        result.Value.AccessTokenExpiry.Should().BeCloseTo(newExpiry, TimeSpan.FromSeconds(2));

        harness.Handler.CallCount.Should().Be(1);
        harness.Handler.LastRequestUri!.AbsolutePath.Should().Be("/auth/refresh");
        harness.Handler.LastRequestBody.Should().Contain("old-refresh", "the old pair is exchanged server-to-server");

        context.Items[CookieTokenReader.FreshAccessTokenItemKey].Should().Be(
            "new-access", "this request's SSR authentication must see the rotated token");
        string setCookies = context.Response.Headers.SetCookie.ToString();
        setCookies.Should().Contain("new-access").And.Contain("new-refresh");
    }

    [Fact]
    public async Task GetOrRefreshAsync_WhenRefreshEndpointFails_ReturnsNullAndWritesNoCookies()
    {
        string expired = CreateJwt(DateTime.UtcNow.AddMinutes(-5));
        using var harness = CreateSut(RespondWith(HttpStatusCode.Unauthorized));
        var context = CreateContext(accessToken: expired, refreshToken: "old-refresh");

        SessionTokenResult? result = await harness.Sut.GetOrRefreshAsync(context);

        result.Should().BeNull();
        context.Response.Headers.SetCookie.Count.Should().Be(0);
        context.Items.Should().NotContainKey(CookieTokenReader.FreshAccessTokenItemKey);
    }

    [Fact]
    public async Task GetOrRefreshAsync_WhenRefreshReturnsEmptyAccessToken_ReturnsNull()
    {
        string expired = CreateJwt(DateTime.UtcNow.AddMinutes(-5));
        using var harness = CreateSut(RespondWithTokens(string.Empty, "new-refresh", DateTime.UtcNow.AddMinutes(15)));
        var context = CreateContext(accessToken: expired, refreshToken: "old-refresh");

        SessionTokenResult? result = await harness.Sut.GetOrRefreshAsync(context);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetOrRefreshAsync_MissingAccessCookieButValidRefreshCookie_StillRefreshes()
    {
        using var harness = CreateSut(RespondWithTokens("new-access", "new-refresh", DateTime.UtcNow.AddMinutes(15)));
        var context = CreateContext(accessToken: null, refreshToken: "old-refresh");

        SessionTokenResult? result = await harness.Sut.GetOrRefreshAsync(context);

        result.Should().NotBeNull();
        result!.Value.AccessToken.Should().Be("new-access");
    }

    // ── Rotation-grace single flight ──
    [Fact]
    public async Task GetOrRefreshAsync_SiblingRequestWithSameOldRefreshToken_UsesCachedRotation()
    {
        string expired = CreateJwt(DateTime.UtcNow.AddMinutes(-5));
        using var harness = CreateSut(RespondWithTokens("new-access", "new-refresh", DateTime.UtcNow.AddMinutes(15)));
        var first = CreateContext(accessToken: expired, refreshToken: "old-refresh");
        var sibling = CreateContext(accessToken: expired, refreshToken: "old-refresh");

        SessionTokenResult? firstResult = await harness.Sut.GetOrRefreshAsync(first);
        SessionTokenResult? siblingResult = await harness.Sut.GetOrRefreshAsync(sibling);

        harness.Handler.CallCount.Should().Be(1, "the rotation-grace cache must prevent double rotation");
        firstResult!.Value.AccessToken.Should().Be("new-access");
        siblingResult!.Value.AccessToken.Should().Be("new-access");
        sibling.Items[CookieTokenReader.FreshAccessTokenItemKey].Should().Be(
            "new-access", "the sibling still gets cookies and the stashed token for its own SSR pass");
    }

    // ── Helpers ──
    private static string CreateJwt(DateTime expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            notBefore: expires.AddMinutes(-30),
            expires: expires));

    private static DefaultHttpContext CreateContext(string? accessToken, string? refreshToken)
    {
        var context = new DefaultHttpContext();
        var cookies = new List<string>();
        if (accessToken is not null)
        {
            cookies.Add($"{SessionCookieEndpoints.AccessTokenCookieName}={accessToken}");
        }

        if (refreshToken is not null)
        {
            cookies.Add($"{SessionCookieEndpoints.RefreshTokenCookieName}={refreshToken}");
        }

        if (cookies.Count > 0)
        {
            context.Request.Headers.Cookie = string.Join("; ", cookies);
        }

        return context;
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> RespondWith(HttpStatusCode statusCode) =>
        _ => new HttpResponseMessage(statusCode);

    private static Func<HttpRequestMessage, HttpResponseMessage> RespondWithTokens(
        string accessToken,
        string refreshToken,
        DateTime accessTokenExpiry) =>
        _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new AuthenticationResponse(accessToken, refreshToken, accessTokenExpiry)),
        };

    private static RefresherHarness CreateSut(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(responder);

    private sealed class RefresherHarness : IDisposable
    {
        private readonly MemoryCache _cache;

        public RefresherHarness(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            Handler = new StubHttpMessageHandler(responder);
            _cache = new MemoryCache(new MemoryCacheOptions());
            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Production);
            Sut = new CookieSessionRefresher(new StubHttpClientFactory(Handler), _cache, environment.Object);
        }

        public CookieSessionRefresher Sut { get; }

        public StubHttpMessageHandler Handler { get; }

        public void Dispose()
        {
            Sut.Dispose();
            Handler.Dispose();
            _cache.Dispose();
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return responder(request);
        }
    }
}
