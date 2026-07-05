using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using MMCA.Common.API.SessionCookies;
using Moq;
using HeaderSameSiteMode = Microsoft.Net.Http.Headers.SameSiteMode;

namespace MMCA.Common.API.Tests.SessionCookies;

/// <summary>
/// Verifies <see cref="SessionCookieJar"/> writes both auth cookies with the exact hardening
/// attributes (HttpOnly, Secure outside Development, SameSite=Lax, Path=/, 7-day Max-Age)
/// and expires them symmetrically on deletion.
/// </summary>
public sealed class SessionCookieJarTests
{
    private static readonly TimeSpan ExpectedLifetime = TimeSpan.FromDays(7);

    // ── Append ──
    [Fact]
    public void Append_WritesBothTokenCookies()
    {
        var context = new DefaultHttpContext();

        SessionCookieJar.Append(context, "access-value", "refresh-value", CreateEnvironment(Environments.Production));

        var cookies = ParseSetCookies(context);
        cookies.Should().HaveCount(2);
        cookies.Should().ContainSingle(c =>
            c.Name == SessionCookieEndpoints.AccessTokenCookieName && c.Value == "access-value");
        cookies.Should().ContainSingle(c =>
            c.Name == SessionCookieEndpoints.RefreshTokenCookieName && c.Value == "refresh-value");
    }

    [Fact]
    public void Append_InProduction_SetsEveryHardeningAttribute()
    {
        var context = new DefaultHttpContext();

        SessionCookieJar.Append(context, "access-value", "refresh-value", CreateEnvironment(Environments.Production));

        foreach (SetCookieHeaderValue cookie in ParseSetCookies(context))
        {
            cookie.HttpOnly.Should().BeTrue("the browser JS must never read the auth cookies");
            cookie.Secure.Should().BeTrue("outside Development the cookies are TLS-only");
            cookie.SameSite.Should().Be(HeaderSameSiteMode.Lax);
            cookie.Path.ToString().Should().Be("/");
            cookie.MaxAge.Should().Be(ExpectedLifetime, "the cookie must not outlive the refresh token it carries");
        }
    }

    [Fact]
    public void Append_InDevelopment_OmitsSecureButKeepsHttpOnly()
    {
        var context = new DefaultHttpContext();

        SessionCookieJar.Append(context, "access-value", "refresh-value", CreateEnvironment(Environments.Development));

        foreach (SetCookieHeaderValue cookie in ParseSetCookies(context))
        {
            cookie.Secure.Should().BeFalse("local http://localhost development has no TLS");
            cookie.HttpOnly.Should().BeTrue();
            cookie.SameSite.Should().Be(HeaderSameSiteMode.Lax);
        }
    }

    // ── Delete ──
    [Fact]
    public void Delete_ExpiresBothCookies()
    {
        var context = new DefaultHttpContext();

        SessionCookieJar.Delete(context, CreateEnvironment(Environments.Production));

        var cookies = ParseSetCookies(context);
        cookies.Should().HaveCount(2);
        cookies.Select(c => c.Name.ToString()).Should().BeEquivalentTo(
            SessionCookieEndpoints.AccessTokenCookieName,
            SessionCookieEndpoints.RefreshTokenCookieName);
        foreach (SetCookieHeaderValue cookie in cookies)
        {
            cookie.Value.ToString().Should().BeEmpty();
            cookie.Expires.Should().Be(DateTimeOffset.UnixEpoch, "an epoch expiry tells the browser to drop the cookie");
            cookie.MaxAge.Should().BeNull("a zero lifetime must not emit a Max-Age attribute");
        }
    }

    [Fact]
    public void Delete_UsesSameScopeAttributesAsAppend()
    {
        // The deletion cookie must match the original cookie's Path (and flags) or the
        // browser treats it as a different cookie and keeps the original.
        var context = new DefaultHttpContext();

        SessionCookieJar.Delete(context, CreateEnvironment(Environments.Production));

        foreach (SetCookieHeaderValue cookie in ParseSetCookies(context))
        {
            cookie.Path.ToString().Should().Be("/");
            cookie.HttpOnly.Should().BeTrue();
            cookie.Secure.Should().BeTrue();
            cookie.SameSite.Should().Be(HeaderSameSiteMode.Lax);
        }
    }

    // ── Helpers ──
    private static IWebHostEnvironment CreateEnvironment(string environmentName)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(environmentName);
        return environment.Object;
    }

    private static IList<SetCookieHeaderValue> ParseSetCookies(HttpContext context) =>
        SetCookieHeaderValue.ParseList(context.Response.Headers.SetCookie.OfType<string>().ToList());
}
