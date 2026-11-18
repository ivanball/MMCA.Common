using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.Tests.Shared.UI;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Web.Services;
using Moq;

namespace MMCA.Common.UI.Web.Tests.Services;

/// <summary>
/// Verifies <see cref="ServerTokenStorageService"/> (Blazor Server cookie-backed token storage):
/// during SSR prerender (a live <see cref="HttpContext"/>) tokens come from the HttpOnly cookies
/// (preferring the freshly-rotated token stashed on <c>HttpContext.Items</c>); on the interactive
/// circuit (no <see cref="HttpContext"/>) the storage keeps the in-memory contract it shares with the
/// WASM storage, run from <see cref="TokenStorageServiceTestsBase"/>; on the hub connection request the
/// circuit actually sees, the token stored at sign-in wins over the connection's cookie.
/// </summary>
public sealed class ServerTokenStorageServiceTests : TokenStorageServiceTestsBase
{
    // CookieSessionRefresher stashes the rotated token under this HttpContext.Items key
    // (CookieTokenReader.FreshAccessTokenItemKey, which is internal to MMCA.Common.API).
    private const string FreshAccessTokenItemKey = "mmca.fresh-access-token";

    private sealed record ServerMocks(
        Mock<IHttpContextAccessor> Accessor,
        Mock<ISessionCookieSync> CookieSync,
        Mock<ITokenRefresher> Refresher);

    private static (ServerTokenStorageService Sut, ServerMocks Mocks) CreateServerSut(
        HttpContext? httpContext = null,
        string? refresherToken = "hydrated-token",
        TimeProvider? timeProvider = null)
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(httpContext);
        var cookieSync = new Mock<ISessionCookieSync>();
        cookieSync.Setup(c => c.SyncAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        cookieSync.Setup(c => c.ClearAsync()).ReturnsAsync(true);
        var refresher = new Mock<ITokenRefresher>();
        refresher
            .Setup(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(refresherToken);
        var sut = new ServerTokenStorageService(
            accessor.Object,
            new CookieTokenReader(accessor.Object),
            cookieSync.Object,
            refresher.Object,
            timeProvider);
        return (sut, new ServerMocks(accessor, cookieSync, refresher));
    }

    private static DefaultHttpContext ContextWithCookies(string? accessToken = null, string? refreshToken = null)
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

    /// <summary>
    /// The HttpContext the circuit actually sees: the request that opened the Blazor hub connection,
    /// whose endpoint carries SignalR's hub metadata and whose cookies are those sent at connect time.
    /// </summary>
    internal static DefaultHttpContext HubConnectionContext(string? accessToken = null)
    {
        var context = ContextWithCookies(accessToken: accessToken);
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new HubMetadata(typeof(ConnectionHub))),
            "/_blazor"));
        return context;
    }

    // == SSR prerender: the HttpOnly cookie is the source of truth ==
    [Fact]
    public async Task GetAccessTokenAsync_DuringSsr_ReadsAccessCookieWithoutRefresher()
    {
        var (sut, mocks) = CreateServerSut(ContextWithCookies(accessToken: "cookie-access-token"));

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be("cookie-access-token");
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAccessTokenAsync_DuringSsr_PrefersFreshlyRotatedTokenStashedOnItems()
    {
        var context = ContextWithCookies(accessToken: "stale-cookie-token");
        context.Items[FreshAccessTokenItemKey] = "rotated-token";
        var (sut, _) = CreateServerSut(context);

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be("rotated-token", "SSR must see the token the refresh middleware just rotated");
    }

    [Fact]
    public async Task GetAccessTokenAsync_DuringSsrWithNoCookie_ReturnsNull()
    {
        var (sut, _) = CreateServerSut(ContextWithCookies());

        var result = await sut.GetAccessTokenAsync();

        result.Should().BeNull();
    }

    // == Run 12 A-03: on the circuit the HttpContext is the hub connection request, opened before sign-in ==
    [Fact]
    public async Task GetAccessTokenAsync_OnHubConnectionWithoutCookie_ReturnsTheTokenStoredAtSignIn()
    {
        var (sut, mocks) = CreateServerSut(HubConnectionContext());
        var freshJwt = CreateJwt(DateTime.UtcNow.AddMinutes(10));

        await sut.SetTokensAsync(freshJwt, "rt");

        (await sut.GetAccessTokenAsync()).Should().Be(freshJwt,
            "the token stored at sign-in must win over the connection request, which carried no cookie");
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAccessTokenAsync_OnHubConnectionWithExpiredStoredToken_RehydratesAndIgnoresTheConnectionCookie()
    {
        var connectionCookieToken = CreateJwt(DateTime.UtcNow.AddMinutes(10));
        var (sut, mocks) = CreateServerSut(HubConnectionContext(connectionCookieToken));
        await sut.SetTokensAsync(CreateJwt(DateTime.UtcNow.AddMinutes(-5)), "rt");

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be("hydrated-token");
        result.Should().NotBe(connectionCookieToken);
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAccessTokenAsync_DuringSsrPrerenderWithNothingInMemory_ReadsTheAccessCookie()
    {
        var cookieToken = CreateJwt(DateTime.UtcNow.AddMinutes(10));
        var (sut, mocks) = CreateServerSut(ContextWithCookies(accessToken: cookieToken));

        (await sut.GetAccessTokenAsync()).Should().Be(cookieToken);
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAccessTokenAsync_OnHubConnectionAfterLogout_DoesNotResurrectTheConnectionCookie()
    {
        var oldToken = CreateJwt(DateTime.UtcNow.AddMinutes(10));
        var (sut, _) = CreateServerSut(HubConnectionContext(oldToken), refresherToken: null);
        await sut.SetTokensAsync(CreateJwt(DateTime.UtcNow.AddMinutes(10)), "rt");

        await sut.ClearTokensAsync();

        (await sut.GetAccessTokenAsync()).Should().NotBe(oldToken,
            "a logout must not be undone by the token the connection request carried");
    }

    [Fact]
    public async Task GetAccessTokenAsync_OnHubConnectionWithExpiredCookie_RehydratesInsteadOfReturningIt()
    {
        // The production loop: a circuit kept sending the connection's expired token for hours.
        var expiredCookieToken = CreateJwt(DateTime.UtcNow.AddMinutes(-5));
        var (sut, mocks) = CreateServerSut(HubConnectionContext(expiredCookieToken));

        (await sut.GetAccessTokenAsync()).Should().Be("hydrated-token");
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAccessTokenAsync_OnHubConnectionWithFreshCookieAndNothingStored_ReturnsTheCookie()
    {
        // A circuit that starts signed in keeps answering from its connection cookie while it is fresh.
        var cookieToken = CreateJwt(DateTime.UtcNow.AddMinutes(10));
        var (sut, mocks) = CreateServerSut(HubConnectionContext(cookieToken));

        (await sut.GetAccessTokenAsync()).Should().Be(cookieToken);
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // == Refresh token exposure ==
    [Fact]
    public async Task GetRefreshTokenAsync_DuringSsr_ReadsRefreshCookie()
    {
        var (sut, _) = CreateServerSut(ContextWithCookies(refreshToken: "cookie-refresh-token"));

        var result = await sut.GetRefreshTokenAsync();

        result.Should().Be("cookie-refresh-token");
    }

    // == A-34: the session the circuit started with ended remotely ==
    [Fact]
    public async Task GetAccessTokenAsync_OnHubConnection_WhenTheSessionTheCircuitStartedWithIsGone_NotifiesLogout()
    {
        // The circuit started signed in from the connection request's cookie and has stored nothing
        // itself; that cookie's token is now inside the refresh skew and the refresh says no session.
        var (sut, provider) = CreateSessionAwareCircuitSut(
            HubConnectionContext(accessToken: CreateJwt(DateTime.UtcNow.AddSeconds(10))),
            TokenAcquisition.NoSession);
        var notifications = 0;
        provider.AuthenticationStateChanged += _ => notifications++;

        (await sut.GetAccessTokenAsync()).Should().BeNull();

        notifications.Should().Be(1);
    }

    protected override ISessionAwareTokenStorage CreateStorage(
        ISessionCookieSync sessionCookieSync,
        ITokenRefresher tokenRefresher,
        TimeProvider? timeProvider)
    {
        // The interactive circuit: no HttpContext, so the in-memory path is the one under test.
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns((HttpContext?)null);
        return new ServerTokenStorageService(
            accessor.Object,
            new CookieTokenReader(accessor.Object),
            sessionCookieSync,
            tokenRefresher,
            timeProvider);
    }

    private static (ServerTokenStorageService Sut, JwtAuthenticationStateProvider Provider) CreateSessionAwareCircuitSut(
        HttpContext? httpContext,
        TokenAcquisition outcome)
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(httpContext);
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.Setup(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        var cookieSync = new Mock<ISessionCookieSync>();
        cookieSync.Setup(c => c.SyncAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var sut = new ServerTokenStorageService(
            accessor.Object,
            new CookieTokenReader(accessor.Object),
            cookieSync.Object,
            refresher.Object,
            timeProvider: null);
        return (sut, new JwtAuthenticationStateProvider(sut));
    }

    /// <summary>Stands in for Blazor's internal ComponentHub in the connection endpoint's metadata.</summary>
    private sealed class ConnectionHub : Hub;
}
