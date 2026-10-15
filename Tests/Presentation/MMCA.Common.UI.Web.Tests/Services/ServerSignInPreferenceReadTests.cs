using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Capabilities.Notifications;
using MMCA.Common.UI.Services.Preferences;
using MMCA.Common.UI.Web.Services;
using Moq;

namespace MMCA.Common.UI.Web.Tests.Services;

/// <summary>
/// Run 12 A-03, end to end on Blazor Server: the FIRST sign-in on a fresh browser must apply the stored
/// language and theme. The circuit's HttpContext is the hub connection request, opened before sign-in
/// and so carrying no auth cookie. The real <see cref="AuthUIService"/> signs in and stores the token
/// in the real <see cref="ServerTokenStorageService"/>, then the real <see cref="ApiUserPreferenceReader"/>
/// reads the preferences through its own HTTP pipeline, whose stub handler records the bearer it sent.
/// </summary>
public sealed class ServerSignInPreferenceReadTests
{
    private static readonly Uri ApiBase = new("https://api.test/");

    [Fact]
    public async Task FirstSignInOnAHubConnectionWithoutCookie_PreferenceReadSendsTheNewBearer()
    {
        var accessToken = ServerTokenStorageServiceTests.CreateJwt(DateTime.UtcNow.AddMinutes(10));
        using var handler = new RecordingHandler(accessToken);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(f => f.CreateClient("APIClient"))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { BaseAddress = ApiBase });

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(ServerTokenStorageServiceTests.HubConnectionContext());
        var cookieSync = new Mock<ISessionCookieSync>();
        cookieSync.Setup(c => c.SyncAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var refresher = new Mock<ITokenRefresher>();
        var storage = new ServerTokenStorageService(
            accessor.Object,
            new CookieTokenReader(accessor.Object),
            cookieSync.Object,
            refresher.Object);

        var auth = new AuthUIService(
            httpClientFactory.Object,
            storage,
            refresher.Object,
            Mock.Of<AuthenticationStateProvider>(),
            Mock.Of<IPushRegistrationService>());
        var reader = new ApiUserPreferenceReader(httpClientFactory.Object, storage);

        var login = await auth.LoginAsync(new LoginRequest("ada@example.com", "secret"));
        login.IsSuccess.Should().BeTrue();

        var preferences = await reader.GetAsync();

        handler.PreferenceAuthorization.Should().Be(
            $"Bearer {accessToken}",
            "the preference read right after sign-in must carry the token the sign-in stored, not the connection request's missing cookie");
        preferences.Culture.Should().Be("fr-FR");
        preferences.Theme.Should().Be("dark");
    }

    /// <summary>Answers the login and preference calls and records the preference read's bearer.</summary>
    private sealed class RecordingHandler(string accessToken) : HttpMessageHandler
    {
        public string? PreferenceAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new AuthenticationResponse(accessToken, "refresh-token", DateTime.UtcNow.AddMinutes(10))),
                });
            }

            if (path.EndsWith("/auth/preferences", StringComparison.Ordinal))
            {
                PreferenceAuthorization = request.Headers.Authorization?.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new UserPreferences("fr-FR", "dark")),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
