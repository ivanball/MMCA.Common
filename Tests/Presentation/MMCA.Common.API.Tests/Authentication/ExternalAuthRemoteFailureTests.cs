using System.Net;
using AspNet.Security.OAuth.Apple;
using AspNet.Security.OAuth.GitHub;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authentication;

namespace MMCA.Common.API.Tests.Authentication;

/// <summary>
/// Pins ADC Run 20 defect D1 (manual cases U-15 and U-26): a provider round trip that fails before the
/// OAuth completion (a forged or stale state, a correlation cookie lost during a long wait, the user
/// cancelling at the provider with <c>error=access_denied</c>) answers a redirect to the login page with
/// <c>?error=oauth_failed</c>, or to the allow-listed native callback, never a 500 problem response.
/// The callbacks are driven through the real authentication middleware on a test server.
/// </summary>
public sealed class ExternalAuthRemoteFailureTests
{
    private const string UiBaseUrl = "https://ui.example";
    private const string WebLoginFailure = UiBaseUrl + "/login?error=oauth_failed";

    public static TheoryData<string> CallbackProviders() => new() { "google", "github" };

    [Theory]
    [MemberData(nameof(CallbackProviders))]
    public async Task Callback_WithAForgedState_RedirectsToTheLoginPageWithTheFailureCode(string provider)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            new Uri($"/auth/callback/{provider}?code=x&state=garbage", UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(WebLoginFailure);
    }

    [Theory]
    [MemberData(nameof(CallbackProviders))]
    public async Task Callback_WhenTheUserCancelsAtTheProvider_RedirectsToTheLoginPageWithTheFailureCode(string provider)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var request = CancelledCallback(app, provider, returnUrl: "/sessions");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(WebLoginFailure);
    }

    [Fact]
    public async Task Callback_WhenTheUserCancelsAStartedNativeSignIn_RedirectsToTheAllowListedNativeCallback()
    {
        await using var app = await StartAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OAuth:AllowedReturnUrlSchemes:0"] = "myapp",
        });
        using var client = app.GetTestClient();
        using var request = CancelledCallback(app, "github", returnUrl: "myapp://oauth-complete");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("myapp://oauth-complete?error=oauth_failed");
    }

    [Fact]
    public async Task Callback_WithACorrelationCookieThatExpired_RedirectsToTheLoginPageWithTheFailureCode()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var request = CancelledCallback(app, "google", returnUrl: "/sessions", sendCorrelationCookie: false);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(WebLoginFailure);
    }

    [Fact]
    public async Task EveryProviderScheme_HandlesItsRemoteFailures()
    {
        await using var app = await StartAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OAuth:Apple:ClientId"] = "com.example.app.signin",
            ["OAuth:Apple:TeamId"] = "TEAM123456",
            ["OAuth:Apple:KeyId"] = "KEY1234567",
            ["OAuth:Apple:PrivateKeyPem"] = "-----BEGIN PRIVATE KEY-----\nnot-a-real-key\n-----END PRIVATE KEY-----",
            ["OAuth:Oidc:contoso:Authority"] = "https://login.contoso.example/tenant-1/v2.0",
            ["OAuth:Oidc:contoso:ClientId"] = "contoso-client-id",
            ["OAuth:Oidc:contoso:ClientSecret"] = "contoso-client-secret",
        });
        var services = app.Services;

        RemoteAuthenticationOptions[] schemes =
        [
            services.GetRequiredService<IOptionsMonitor<GoogleOptions>>().Get(GoogleDefaults.AuthenticationScheme),
            services.GetRequiredService<IOptionsMonitor<GitHubAuthenticationOptions>>().Get(GitHubAuthenticationDefaults.AuthenticationScheme),
            services.GetRequiredService<IOptionsMonitor<AppleAuthenticationOptions>>().Get(AppleAuthenticationDefaults.AuthenticationScheme),
            services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("contoso"),
        ];

        schemes.Should().AllSatisfy(options =>
            options.Events.OnRemoteFailure.Should().Be((Func<RemoteFailureContext, Task>)OAuthReturnUrls.RedirectRemoteFailureAsync));
    }

    // A callback carrying a state the scheme itself protected, as the provider returns it when the user
    // cancels: error=access_denied and no code. The correlation cookie is the one the challenge set.
    private static HttpRequestMessage CancelledCallback(
        WebApplication app,
        string provider,
        string returnUrl,
        bool sendCorrelationCookie = true)
    {
        const string nonce = "correlation-nonce";
        var options = provider == "google"
            ? (OAuthOptions)app.Services.GetRequiredService<IOptionsMonitor<GoogleOptions>>().Get(GoogleDefaults.AuthenticationScheme)
            : app.Services.GetRequiredService<IOptionsMonitor<GitHubAuthenticationOptions>>().Get(GitHubAuthenticationDefaults.AuthenticationScheme);

        var properties = new AuthenticationProperties { RedirectUri = "/auth/oauth/complete" };
        properties.Items["returnUrl"] = returnUrl;
        properties.Items[".xsrf"] = nonce;
        var state = Uri.EscapeDataString(options.StateDataFormat.Protect(properties));

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/auth/callback/{provider}?error=access_denied&state={state}", UriKind.Relative));
        if (sendCorrelationCookie)
        {
            request.Headers.Add("Cookie", $"{options.CorrelationCookie.Name}{nonce}=N");
        }

        return request;
    }

    private static async Task<WebApplication> StartAsync(Dictionary<string, string?>? extra = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OAuth:UIBaseUrl"] = UiBaseUrl + "/",
            ["OAuth:Google:ClientId"] = "google-client-id",
            ["OAuth:Google:ClientSecret"] = "google-client-secret",
            ["OAuth:GitHub:ClientId"] = "github-client-id",
            ["OAuth:GitHub:ClientSecret"] = "github-client-secret",
        });
        if (extra is not null)
        {
            builder.Configuration.AddInMemoryCollection(extra);
        }

        builder.Services.AddAuthentication();
        builder.Services.AddExternalAuthProviders(builder.Configuration);

        var app = builder.Build();
        app.UseAuthentication();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
