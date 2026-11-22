using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Gallery;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.Auth;

/// <summary>
/// The two-factor opt-in over real HTTP (design 5.5, <see cref="NotificationPagesHiddenRoutingTests"/>
/// pattern): in a host that never called <c>AddTwoFactorUI()</c> (the gallery with
/// <c>Gallery:TwoFactorUI=false</c>), <c>/profile/two-factor</c> answers 404 with the not-found page,
/// signed in or out, and carries no two-factor nav item; a host that called it keeps the page's own
/// authorization (the gallery's scheme answers a challenge with 401) and serves the page signed in.
/// </summary>
public sealed class TwoFactorPageHiddenRoutingTests
{
    private const string Route = "/profile/two-factor";
    private const string WithoutTwoFactorUI = "--Gallery:TwoFactorUI=false";
    private const string SignedInCookie = "gallery_auth=1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutTwoFactorUI_AnswersNotFound(bool signedIn)
    {
        await using var host = await GalleryProcess.StartAsync(WithoutTwoFactorUI);

        using var response = await host.GetAsync(Route, signedIn);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "a host that adopts nothing has no two-factor page");
        body.Should().Contain("Page Not Found");
        body.Should().NotContain("href=\"/profile/two-factor\"", "and no nav item that points at it");
    }

    [Fact]
    public async Task WithTwoFactorUI_SignedOut_IsStillChallenged()
    {
        await using var host = await GalleryProcess.StartAsync();

        using var response = await host.GetAsync(Route, signedIn: false);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the page is a signed-in page in a host that serves it");
    }

    [Fact]
    public async Task WithTwoFactorUI_SignedIn_ServesThePage()
    {
        await using var host = await GalleryProcess.StartAsync();

        using var response = await host.GetAsync(Route, signedIn: true);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Two-factor authentication");
        body.Should().NotContain("Page Not Found");
        body.Should().Contain("href=\"/profile/two-factor\"", "the module's nav item is rendered for a signed-in user");
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
