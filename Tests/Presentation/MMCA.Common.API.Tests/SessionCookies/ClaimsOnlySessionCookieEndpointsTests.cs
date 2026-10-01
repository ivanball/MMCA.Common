using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.API.SessionCookies;

namespace MMCA.Common.API.Tests.SessionCookies;

/// <summary>
/// The session-cookie endpoints under <see cref="SessionCookieSettings.ClaimsOnlyBrowserTokens"/> (set
/// by the same-origin API proxy): the token endpoint hands script a claims-only token instead of the
/// credential, the cookie-seeding POST ignores tokens posted by script, and the cookies carry the
/// configured SameSite. The default mode is pinned by <see cref="SessionCookieEndpointsTests"/>.
/// </summary>
public sealed class ClaimsOnlySessionCookieEndpointsTests
{
    [Fact]
    public async Task PostSessionToken_ClaimsOnly_ReturnsAnUnsignedClaimsCopy_NeverTheCredential()
    {
        var accessToken = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim("sub", "42")],
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("claims-only-endpoint-tests-key-long-enough-for-hs256")),
                SecurityAlgorithms.HmacSha256)));
        using var host = await CreateHostAsync(claimsOnly: true, new SessionTokenResult(accessToken, DateTime.UtcNow.AddMinutes(10)));
        using HttpClient client = host.GetTestClient();

        using var response = await client.PostAsync(new Uri("/auth/session/token", UriKind.Relative), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<SessionTokenResponse>(TestContext.Current.CancellationToken);
        payload!.AccessToken.Should().NotBe(accessToken).And.EndWith(".");
        new JwtSecurityTokenHandler().ReadJwtToken(payload.AccessToken).Subject.Should().Be("42");
    }

    [Fact]
    public async Task PostSessionCookie_ClaimsOnly_IgnoresScriptSuppliedTokens()
    {
        using var host = await CreateHostAsync(claimsOnly: true, result: null);
        using HttpClient client = host.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/auth/session-cookie",
            new SessionCookieEndpoints.SessionCookieRequest("forged-access", "forged-refresh"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.Contains("Set-Cookie").Should().BeFalse("only the server writes the cookies in claims-only mode");
    }

    [Fact]
    public async Task DeleteSessionCookie_UsesTheConfiguredSameSite()
    {
        using var host = await CreateHostAsync(claimsOnly: true, result: null);
        using HttpClient client = host.GetTestClient();

        using var response = await client.DeleteAsync(new Uri("/auth/session-cookie", UriKind.Relative), TestContext.Current.CancellationToken);

        response.Headers.GetValues("Set-Cookie").Should().OnlyContain(c => c.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IHost> CreateHostAsync(bool claimsOnly, SessionTokenResult? result) =>
        await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .UseEnvironment(Environments.Production)
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<ICookieSessionRefresher>(new StubRefresher(result));
                    services.Configure<SessionCookieSettings>(settings =>
                    {
                        settings.ClaimsOnlyBrowserTokens = claimsOnly;
                        settings.SameSite = SameSiteMode.Strict;
                    });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapSessionCookieEndpoints());
                }))
            .StartAsync();

    private sealed class StubRefresher(SessionTokenResult? result) : ICookieSessionRefresher
    {
        public Task<SessionTokenResult?> GetOrRefreshAsync(HttpContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public Task<SessionTokenResult?> RefreshAsync(HttpContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
