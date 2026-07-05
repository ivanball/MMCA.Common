using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.API.SessionCookies;
using Moq;

namespace MMCA.Common.API.Tests.SessionCookies;

/// <summary>
/// Verifies <see cref="SessionCookieAuthenticationHandler"/>: claims flow from the session
/// cookie's JWT into <c>HttpContext.User</c> during SSR, expired or malformed tokens fail
/// authentication (rather than silently succeeding), a missing cookie yields NoResult so other
/// schemes can run, and challenge/forbid produce the login redirect and 403 respectively.
/// </summary>
public sealed class SessionCookieAuthenticationHandlerTests
{
    // ── Authenticate ──
    [Fact]
    public async Task AuthenticateAsync_WhenNoCookie_ReturnsNoResult()
    {
        var context = new DefaultHttpContext();
        var sut = await CreateSutAsync(context);

        AuthenticateResult result = await sut.AuthenticateAsync();

        result.None.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTokenValid_SucceedsWithJwtClaims()
    {
        string token = CreateJwt(
            expires: DateTime.UtcNow.AddMinutes(10),
            new Claim("user_id", "42"),
            new Claim("email", "user@example.com"));
        var context = CreateContextWithAccessCookie(token);
        var sut = await CreateSutAsync(context);

        AuthenticateResult result = await sut.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        ClaimsPrincipal principal = result.Ticket!.Principal;
        principal.FindFirst("user_id")!.Value.Should().Be("42");
        principal.FindFirst("email")!.Value.Should().Be("user@example.com");
        principal.Identity!.IsAuthenticated.Should().BeTrue();
        principal.Identity.AuthenticationType.Should().Be(SessionCookieAuthenticationHandler.SchemeName);
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTokenExpired_Fails()
    {
        string token = CreateJwt(expires: DateTime.UtcNow.AddMinutes(-5), new Claim("user_id", "42"));
        var context = CreateContextWithAccessCookie(token);
        var sut = await CreateSutAsync(context);

        AuthenticateResult result = await sut.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Session cookie JWT is expired.");
    }

    [Fact]
    public async Task AuthenticateAsync_WhenCookieIsNotAJwt_Fails()
    {
        var context = CreateContextWithAccessCookie("definitely-not-a-jwt");
        var sut = await CreateSutAsync(context);

        AuthenticateResult result = await sut.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Session cookie is not a valid JWT.");
    }

    [Fact]
    public async Task AuthenticateAsync_WhenJwtSegmentsAreGarbage_FailsInsteadOfThrowing()
    {
        // Three base64url-shaped segments that decode to non-JSON garbage.
        var context = CreateContextWithAccessCookie("aaaa.bbbb.cccc");
        var sut = await CreateSutAsync(context);

        AuthenticateResult result = await sut.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_WhenFreshTokenStashedOnItems_UsesItOverExpiredCookie()
    {
        // CookieSessionRefresher stashes the rotated token on Items for the current request;
        // authentication must pick it up instead of the still-expired request cookie.
        string expired = CreateJwt(expires: DateTime.UtcNow.AddMinutes(-5), new Claim("user_id", "42"));
        string fresh = CreateJwt(expires: DateTime.UtcNow.AddMinutes(10), new Claim("user_id", "42"));
        var context = CreateContextWithAccessCookie(expired);
        context.Items[CookieTokenReader.FreshAccessTokenItemKey] = fresh;
        var sut = await CreateSutAsync(context);

        AuthenticateResult result = await sut.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        result.Ticket!.Principal.FindFirst("user_id")!.Value.Should().Be("42");
    }

    [Fact]
    public async Task AuthenticateAsync_ExpiryUsesTheHandlerTimeProvider_NotTheSystemClock()
    {
        // A token long expired by the REAL clock but still valid on the injected clock must
        // authenticate: the expiry check consults the AuthenticationHandler's TimeProvider
        // (options.TimeProvider), not DateTime.UtcNow, so it is deterministic and consistent
        // with the rest of the authentication stack.
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2000, 1, 1, 23, 45, 0, TimeSpan.Zero));
        string token = CreateJwt(
            expires: new DateTime(2000, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            new Claim("user_id", "42"));
        var context = CreateContextWithAccessCookie(token);
        var sut = await CreateSutAsync(context, new AuthenticationSchemeOptions { TimeProvider = fakeTime });

        AuthenticateResult result = await sut.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        result.Ticket!.Principal.FindFirst("user_id")!.Value.Should().Be("42");
    }

    // ── Challenge / Forbid ──
    [Fact]
    public async Task ChallengeAsync_RedirectsToLoginWithEscapedReturnUrl()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/secure/page";
        context.Request.QueryString = new QueryString("?tab=2");
        var sut = await CreateSutAsync(context);

        await sut.ChallengeAsync(properties: null);

        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        context.Response.Headers.Location.ToString()
            .Should().Be("/login?returnUrl=%2Fsecure%2Fpage%3Ftab%3D2");
    }

    [Fact]
    public async Task ForbidAsync_Returns403()
    {
        var context = new DefaultHttpContext();
        var sut = await CreateSutAsync(context);

        await sut.ForbidAsync(properties: null);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // ── Helpers ──
    private static DefaultHttpContext CreateContextWithAccessCookie(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{SessionCookieEndpoints.AccessTokenCookieName}={token}";
        return context;
    }

    private static string CreateJwt(DateTime expires, params Claim[] claims) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: claims,
            notBefore: expires.AddMinutes(-30),
            expires: expires));

    private static async Task<SessionCookieAuthenticationHandler> CreateSutAsync(
        DefaultHttpContext context,
        AuthenticationSchemeOptions? schemeOptions = null)
    {
        var options = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options
            .Setup(x => x.Get(SessionCookieAuthenticationHandler.SchemeName))
            .Returns(schemeOptions ?? new AuthenticationSchemeOptions());

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(x => x.HttpContext).Returns(context);

        var sut = new SessionCookieAuthenticationHandler(
            options.Object,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            new CookieTokenReader(accessor.Object));

        var scheme = new AuthenticationScheme(
            SessionCookieAuthenticationHandler.SchemeName,
            displayName: null,
            typeof(SessionCookieAuthenticationHandler));
        await sut.InitializeAsync(scheme, context);

        return sut;
    }
}
