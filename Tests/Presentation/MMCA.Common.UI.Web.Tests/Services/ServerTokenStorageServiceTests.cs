using System.IdentityModel.Tokens.Jwt;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Web.Services;
using Moq;

namespace MMCA.Common.UI.Web.Tests.Services;

/// <summary>
/// Verifies <see cref="ServerTokenStorageService"/> (Blazor Server cookie-backed token storage):
/// during SSR prerender (a live <see cref="HttpContext"/>) tokens come from the HttpOnly cookies
/// (preferring the freshly-rotated token stashed on <c>HttpContext.Items</c>); on the interactive
/// circuit (no <see cref="HttpContext"/>) the access token is held in memory, hydrated single-flight
/// via <see cref="ITokenRefresher"/>, and refreshed proactively near expiry; the refresh token is
/// never readable on the circuit; set/clear round-trip through <see cref="ISessionCookieSync"/>.
/// </summary>
public sealed class ServerTokenStorageServiceTests
{
    // CookieSessionRefresher stashes the rotated token under this HttpContext.Items key
    // (CookieTokenReader.FreshAccessTokenItemKey, which is internal to MMCA.Common.API).
    private const string FreshAccessTokenItemKey = "mmca.fresh-access-token";

    private sealed record Mocks(
        Mock<IHttpContextAccessor> Accessor,
        Mock<ISessionCookieSync> CookieSync,
        Mock<ITokenRefresher> Refresher);

    private static (ServerTokenStorageService Sut, Mocks Mocks) CreateSut(
        HttpContext? httpContext = null,
        string? refresherToken = "hydrated-token")
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(httpContext);
        var cookieSync = new Mock<ISessionCookieSync>();
        var refresher = new Mock<ITokenRefresher>();
        refresher
            .Setup(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(refresherToken);
        var sut = new ServerTokenStorageService(
            accessor.Object,
            new CookieTokenReader(accessor.Object),
            cookieSync.Object,
            refresher.Object);
        return (sut, new Mocks(accessor, cookieSync, refresher));
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

    private static string CreateJwt(DateTime expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            notBefore: expires.AddMinutes(-60),
            expires: expires));

    // == SSR prerender: the HttpOnly cookie is the source of truth ==
    [Fact]
    public async Task GetAccessTokenAsync_DuringSsr_ReadsAccessCookieWithoutRefresher()
    {
        var (sut, mocks) = CreateSut(ContextWithCookies(accessToken: "cookie-access-token"));

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be("cookie-access-token");
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAccessTokenAsync_DuringSsr_PrefersFreshlyRotatedTokenStashedOnItems()
    {
        var context = ContextWithCookies(accessToken: "stale-cookie-token");
        context.Items[FreshAccessTokenItemKey] = "rotated-token";
        var (sut, _) = CreateSut(context);

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be("rotated-token", "SSR must see the token the refresh middleware just rotated");
    }

    [Fact]
    public async Task GetAccessTokenAsync_DuringSsrWithNoCookie_ReturnsNull()
    {
        var (sut, _) = CreateSut(ContextWithCookies());

        var result = await sut.GetAccessTokenAsync();

        result.Should().BeNull();
    }

    // == Interactive circuit: in-memory token hydrated via the refresher ==
    [Fact]
    public async Task GetAccessTokenAsync_OnCircuit_HydratesViaRefresher()
    {
        var (sut, mocks) = CreateSut(httpContext: null);

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be("hydrated-token");
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAccessTokenAsync_OnCircuitWithFreshInMemoryJwt_SkipsRefresher()
    {
        var (sut, mocks) = CreateSut(httpContext: null);
        var freshJwt = CreateJwt(DateTime.UtcNow.AddMinutes(10));
        await sut.SetTokensAsync(freshJwt, "refresh-token");

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be(freshJwt);
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAccessTokenAsync_OnCircuitWithJwtInsideExpirySkew_RefreshesProactively()
    {
        // Valid for 10 more seconds, but the 30-second skew treats it as expired.
        var (sut, mocks) = CreateSut(httpContext: null);
        await sut.SetTokensAsync(CreateJwt(DateTime.UtcNow.AddSeconds(10)), "refresh-token");

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be("hydrated-token");
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAccessTokenAsync_OnCircuit_ConcurrentCallersShareOneSingleFlightAcquisition()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns((HttpContext?)null);
        var refresher = new Mock<ITokenRefresher>();
        var pendingAcquisition = new TaskCompletionSource<string?>();
        refresher
            .Setup(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .Returns(pendingAcquisition.Task);
        var sut = new ServerTokenStorageService(
            accessor.Object,
            new CookieTokenReader(accessor.Object),
            new Mock<ISessionCookieSync>().Object,
            refresher.Object);

        var first = sut.GetAccessTokenAsync();
        var second = sut.GetAccessTokenAsync();
        pendingAcquisition.SetResult("hydrated-token");

        (await first).Should().Be("hydrated-token");
        (await second).Should().Be("hydrated-token");
        refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // == Refresh token exposure ==
    [Fact]
    public async Task GetRefreshTokenAsync_DuringSsr_ReadsRefreshCookie()
    {
        var (sut, _) = CreateSut(ContextWithCookies(refreshToken: "cookie-refresh-token"));

        var result = await sut.GetRefreshTokenAsync();

        result.Should().Be("cookie-refresh-token");
    }

    [Fact]
    public async Task GetRefreshTokenAsync_OnCircuit_ReturnsNull()
    {
        var (sut, _) = CreateSut(httpContext: null);

        var result = await sut.GetRefreshTokenAsync();

        result.Should().BeNull("the HttpOnly refresh cookie must never be readable on the circuit");
    }

    // == Set / clear round-trip through the cookie sync ==
    [Fact]
    public async Task SetTokensAsync_SeedsHttpOnlyCookiesViaSessionSync()
    {
        var (sut, mocks) = CreateSut(httpContext: null);

        await sut.SetTokensAsync("access-token", "refresh-token");

        mocks.CookieSync.Verify(c => c.SyncAsync("access-token", "refresh-token"), Times.Once);
    }

    [Fact]
    public async Task ClearTokensAsync_ClearsMemoryAndCookies()
    {
        var (sut, mocks) = CreateSut(httpContext: null);
        await sut.SetTokensAsync(CreateJwt(DateTime.UtcNow.AddMinutes(10)), "refresh-token");

        await sut.ClearTokensAsync();
        var afterClear = await sut.GetAccessTokenAsync();

        mocks.CookieSync.Verify(c => c.ClearAsync(), Times.Once);
        afterClear.Should().Be("hydrated-token", "a cleared in-memory token must re-hydrate from the cookie session");
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
