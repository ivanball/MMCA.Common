using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Net.Http.Headers;

namespace MMCA.Common.UI.Web.Tests.Hosting;

/// <summary>
/// Pins the antiforgery cookie of the composed Blazor web host outside Development (item
/// B5, D-01) for the deployed shape: TLS ends at an ingress that forwards the original scheme in
/// <c>X-Forwarded-Proto</c>, and the request reaches the host over plain http.
/// </summary>
public sealed class AntiforgeryCookiePolicyTests
{
    private const string TokenPath = "/antiforgery-token";

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task OutsideDevelopment_BehindATlsTerminatingIngress_TheAntiforgeryCookieIsSecure(string environmentName)
    {
        await using var app = await BlazorWebHostHarness.StartAsync(
            BlazorWebHostHarness.CreateBuilder(environmentName),
            afterPipeline: web => web.MapGet(TokenPath, (HttpContext context, IAntiforgery antiforgery) =>
            {
                antiforgery.GetAndStoreTokens(context);
                return Results.Text("ok");
            }).AllowAnonymous());
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(TokenPath, UriKind.Relative));
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.40");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        client.BaseAddress!.Scheme.Should().Be(Uri.UriSchemeHttp, "the hop from the ingress to the host is plain http");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues(HeaderNames.SetCookie, out var setCookies).Should().BeTrue("the antiforgery cookie is issued");
        var antiforgeryCookie = SetCookieHeaderValue.ParseList([.. setCookies!]).Should()
            .ContainSingle(cookie => cookie.Name.Value!.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal))
            .Which;
        antiforgeryCookie.Secure.Should().BeTrue("outside Development the antiforgery cookie is always Secure");
    }
}
