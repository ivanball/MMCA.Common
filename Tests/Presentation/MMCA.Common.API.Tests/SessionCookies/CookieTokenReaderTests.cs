using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.API.SessionCookies;
using Moq;

namespace MMCA.Common.API.Tests.SessionCookies;

/// <summary>
/// Verifies <see cref="CookieTokenReader"/> reads the auth cookies written by
/// <see cref="SessionCookieEndpoints"/>, prefers a freshly-refreshed access token stashed on
/// <see cref="HttpContext.Items"/> by <see cref="CookieSessionRefresher"/>, and degrades to
/// null when no HttpContext is available (e.g. outside a request).
/// </summary>
public sealed class CookieTokenReaderTests
{
    // ── Access token ──
    [Fact]
    public void ReadAccessToken_WhenNoHttpContext_ReturnsNull()
    {
        var sut = CreateSut(context: null);

        sut.ReadAccessToken().Should().BeNull();
    }

    [Fact]
    public void ReadAccessToken_WhenCookieMissing_ReturnsNull()
    {
        var sut = CreateSut(new DefaultHttpContext());

        sut.ReadAccessToken().Should().BeNull();
    }

    [Fact]
    public void ReadAccessToken_WhenCookiePresent_ReturnsCookieValue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{SessionCookieEndpoints.AccessTokenCookieName}=the-access-token";
        var sut = CreateSut(context);

        sut.ReadAccessToken().Should().Be("the-access-token");
    }

    [Fact]
    public void ReadAccessToken_WhenFreshTokenStashed_PrefersItOverCookie()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{SessionCookieEndpoints.AccessTokenCookieName}=stale-cookie-token";
        context.Items[CookieTokenReader.FreshAccessTokenItemKey] = "fresh-refreshed-token";
        var sut = CreateSut(context);

        sut.ReadAccessToken().Should().Be(
            "fresh-refreshed-token",
            "the Set-Cookie from a refresh only affects subsequent requests, so this request must use the stashed token");
    }

    [Fact]
    public void ReadAccessToken_WhenStashedTokenWhitespace_FallsBackToCookie()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{SessionCookieEndpoints.AccessTokenCookieName}=cookie-token";
        context.Items[CookieTokenReader.FreshAccessTokenItemKey] = "   ";
        var sut = CreateSut(context);

        sut.ReadAccessToken().Should().Be("cookie-token");
    }

    [Fact]
    public void ReadAccessToken_WhenStashedItemNotAString_FallsBackToCookie()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{SessionCookieEndpoints.AccessTokenCookieName}=cookie-token";
        context.Items[CookieTokenReader.FreshAccessTokenItemKey] = 42;
        var sut = CreateSut(context);

        sut.ReadAccessToken().Should().Be("cookie-token");
    }

    // ── Refresh token ──
    [Fact]
    public void ReadRefreshToken_WhenNoHttpContext_ReturnsNull()
    {
        var sut = CreateSut(context: null);

        sut.ReadRefreshToken().Should().BeNull();
    }

    [Fact]
    public void ReadRefreshToken_WhenCookiePresent_ReturnsCookieValue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{SessionCookieEndpoints.RefreshTokenCookieName}=the-refresh-token";
        var sut = CreateSut(context);

        sut.ReadRefreshToken().Should().Be("the-refresh-token");
    }

    [Fact]
    public void ReadRefreshToken_IgnoresStashedFreshAccessToken()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{SessionCookieEndpoints.RefreshTokenCookieName}=cookie-refresh";
        context.Items[CookieTokenReader.FreshAccessTokenItemKey] = "fresh-access";
        var sut = CreateSut(context);

        sut.ReadRefreshToken().Should().Be("cookie-refresh");
    }

    // ── Helpers ──
    private static CookieTokenReader CreateSut(HttpContext? context)
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(x => x.HttpContext).Returns(context);
        return new CookieTokenReader(accessor.Object);
    }
}
