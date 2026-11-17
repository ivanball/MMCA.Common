using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Gallery;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests;

/// <summary>
/// U-40 (Store run 1): in a host that never called <c>AddNotificationUI()</c>, a signed-out request for <c>/notifications</c> or
/// <c>/notifications/inbox</c> was answered by the authentication challenge (a redirect to
/// <c>/login</c> in the real hosts) instead of the not-found page: the gate lived inside the Blazor
/// router, which runs only after the authorization middleware has already challenged the pages'
/// <c>[Authorize]</c> endpoint metadata. Without the notification UI both routes must answer 404,
/// signed in or out; a host that registered it keeps the pages' own authorization.
/// <para>
/// Plain HTTP against the self-hosted gallery (real Razor Components endpoints, real authorization
/// middleware, the shared <c>Routes.razor</c>), so no browser is needed. The gallery registers the
/// notification UI module by default; <c>Gallery:NotificationUI=false</c> on the command line leaves it
/// out, which is exactly the "never called AddNotificationUI()" host. Its auth scheme answers a
/// challenge with 401 rather than a redirect, so "served with authorization" is 401 here.
/// </para>
/// </summary>
public sealed class NotificationPagesHiddenRoutingTests
{
    private const string WithoutNotificationUI = "--Gallery:NotificationUI=false";
    private const string SignedInCookie = "gallery_auth=1";

    [Theory]
    [InlineData("/notifications")]
    [InlineData("/notifications/inbox")]
    public async Task WithoutNotificationUI_SignedOut_AnswersNotFound(string path)
    {
        await using var host = await GalleryProcess.StartAsync(WithoutNotificationUI);

        using var response = await host.GetAsync(path, signedIn: false);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a hidden notification page is not-found for everyone; challenging it first sends a signed-out visitor to the sign-in page for a page that does not exist");
    }

    [Theory]
    [InlineData("/notifications")]
    [InlineData("/notifications/inbox")]
    public async Task WithoutNotificationUI_SignedIn_AnswersNotFound(string path)
    {
        await using var host = await GalleryProcess.StartAsync(WithoutNotificationUI);

        using var response = await host.GetAsync(path, signedIn: true);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "the hidden page renders the not-found page, and the not-found page answers 404");
    }

    [Theory]
    [InlineData("/notifications")]
    [InlineData("/notifications/inbox")]
    public async Task WithNotificationUI_SignedOut_IsStillChallenged(string path)
    {
        await using var host = await GalleryProcess.StartAsync();

        using var response = await host.GetAsync(path, signedIn: false);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "a host with the notification UI keeps the pages' [Authorize] challenge");
    }

    [Theory]
    [InlineData("/notifications")]
    [InlineData("/notifications/inbox")]
    public async Task WithNotificationUI_SignedIn_StillServesThePage(string path)
    {
        await using var host = await GalleryProcess.StartAsync();

        using var response = await host.GetAsync(path, signedIn: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a host with the notification UI serves the pages");
    }

    /// <summary>The gallery self-hosted on an ephemeral port, plus a non-redirecting client.</summary>
    private sealed class GalleryProcess : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private GalleryProcess(WebApplication app, Uri baseAddress)
        {
            _app = app;
            _client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                CheckCertificateRevocationList = true,
            })
            {
                BaseAddress = baseAddress,
            };
        }

        public static async Task<GalleryProcess> StartAsync(params string[] args)
        {
            var app = GalleryHost.BuildApp(args);
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync(TestContext.Current.CancellationToken);

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            return new GalleryProcess(app, new Uri(address));
        }

        public async Task<HttpResponseMessage> GetAsync(string path, bool signedIn)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            if (signedIn)
            {
                request.Headers.Add("Cookie", SignedInCookie);
            }

            return await _client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync(TestContext.Current.CancellationToken);
            await _app.DisposeAsync();
        }
    }
}
