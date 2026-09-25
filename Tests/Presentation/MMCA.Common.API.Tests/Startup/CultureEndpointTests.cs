using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// In-memory host tests for <c>MapCultureEndpoint</c> (ADR-027): the parameterless overload keeps
/// the culture cookie readable by script (the WASM client reads it on startup), the
/// <c>httpOnly</c> overload lets a Server-only host keep it out of script reach, and an unsupported
/// culture writes no cookie at all.
/// </summary>
public sealed class CultureEndpointTests
{
    private const string CultureCookieName = ".AspNetCore.Culture";

    [Fact]
    public async Task MapCultureEndpoint_ByDefault_WritesANonHttpOnlyCookieAndRedirects()
    {
        await using var app = await StartAsync(static app => app.MapCultureEndpoint());
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            new Uri("/culture/set?culture=es&redirectUri=%2Fsessions", UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/sessions");
        var cookie = CultureCookie(response);
        cookie.Should().Contain("c%3Des%7Cuic%3Des");
        cookie.Should().NotContainEquivalentOf("httponly");
        cookie.Should().ContainEquivalentOf("secure");
    }

    [Fact]
    public async Task MapCultureEndpoint_WithHttpOnly_WritesAnHttpOnlyCookie()
    {
        await using var app = await StartAsync(static app => app.MapCultureEndpoint(httpOnly: true));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            new Uri("/culture/set?culture=es", UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/");
        CultureCookie(response).Should().ContainEquivalentOf("httponly");
    }

    [Fact]
    public async Task MapCultureEndpoint_WithAnUnsupportedCulture_WritesNoCookie()
    {
        await using var app = await StartAsync(static app => app.MapCultureEndpoint(httpOnly: true));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            new Uri("/culture/set?culture=xx-YY", UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse();
    }

    private static string CultureCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(CultureCookieName, StringComparison.Ordinal));

    private static async Task<WebApplication> StartAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
