using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MMCA.Common.API.Authentication;
using MMCA.Common.API.Controllers;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.Interfaces;
using Moq;
using IAuthenticationService = MMCA.Common.Application.Auth.IAuthenticationService;

namespace MMCA.Common.API.Tests.Controllers.Auth;

/// <summary>
/// The generic OIDC routes on <see cref="OAuthControllerBase"/>, driven through real routing:
/// <c>GET auth/oauth/{name}</c> challenges a configured OIDC scheme and answers 404 for any other
/// name, the literal google/github/apple routes keep precedence, and
/// <c>GET auth/oauth/discover?email=</c> maps a configured email domain to its provider.
/// </summary>
public sealed class OAuthControllerBaseOidcTests
{
    private const string ContosoAuthorizeEndpoint = "https://login.contoso.example/oauth2/authorize";

    private static Dictionary<string, string?> Configuration(bool withGoogle = false)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OAuth:UIBaseUrl"] = "https://ui.example.com",
            ["OAuth:Oidc:contoso:Authority"] = "https://login.contoso.example",
            ["OAuth:Oidc:contoso:ClientId"] = "contoso-client-id",
            ["OAuth:Oidc:contoso:ClientSecret"] = "contoso-client-secret",
            ["OAuth:Oidc:contoso:DisplayName"] = "Contoso",
            ["OAuth:Oidc:contoso:Domains:0"] = "contoso.com",
            ["OAuth:Oidc:contoso:Domains:1"] = "contoso.co.uk",
        };
        if (withGoogle)
        {
            values["OAuth:Google:ClientId"] = "google-client-id";
            values["OAuth:Google:ClientSecret"] = "google-client-secret";
        }

        return values;
    }

    private static async Task<WebApplication> CreateHostAsync(bool withGoogle = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(Configuration(withGoogle));

        builder.Services.AddSingleton(Mock.Of<IAuthenticationService>());
        builder.Services.AddSingleton(Mock.Of<ICacheService>());
        builder.Services.AddAuthentication();
        builder.Services.AddExternalAuthProviders(builder.Configuration);

        // Static discovery document, so the challenge never reaches out to the authority.
        builder.Services.Configure<OpenIdConnectOptions>("contoso", options =>
            options.Configuration = new OpenIdConnectConfiguration
            {
                Issuer = "https://login.contoso.example",
                AuthorizationEndpoint = ContosoAuthorizeEndpoint,
                TokenEndpoint = "https://login.contoso.example/oauth2/token",
            });

        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (var i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                {
                    manager.FeatureProviders.RemoveAt(i);
                }
            }

            manager.FeatureProviders.Add(new RoutedOAuthControllerFeatureProvider());
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.MapControllers();
        await app.StartAsync(Xunit.TestContext.Current.CancellationToken);
        return app;
    }

    // ── GET auth/oauth/{name} ──
    [Fact]
    public async Task Get_ConfiguredOidcName_ChallengesThatSchemeWithCodeFlowAndPkce()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            new Uri("/auth/oauth/contoso?returnUrl=%2Fdashboard", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith(ContosoAuthorizeEndpoint);
        location.Should().Contain("response_type=code");
        location.Should().Contain("code_challenge=");
        location.Should().Contain("client_id=contoso-client-id");
        location.Should().Contain(Uri.EscapeDataString("/auth/callback/contoso"));
    }

    [Fact]
    public async Task Get_UnknownName_Returns404()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            new Uri("/auth/oauth/fabrikam", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_Google_KeepsTheLiteralRoute()
    {
        await using var app = await CreateHostAsync(withGoogle: true);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            new Uri("/auth/oauth/google", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.Host.Should().Be("accounts.google.com");
    }

    // ── GET auth/oauth/discover?email= ──
    [Theory]
    [InlineData("jane@contoso.com")]
    [InlineData("Jane@CONTOSO.com")]
    [InlineData("jane@contoso.co.uk")]
    public async Task Discover_ConfiguredDomain_ReturnsTheProviderAndDisplayName(string email)
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            new Uri($"/auth/oauth/discover?email={Uri.EscapeDataString(email)}", UriKind.Relative),
            Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DiscoveryBody>(Xunit.TestContext.Current.CancellationToken);
        body.Should().Be(new DiscoveryBody("contoso", "Contoso"));
    }

    [Theory]
    [InlineData("jane@fabrikam.com")]
    [InlineData("jane@sub.contoso.com")]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task Discover_UnconfiguredDomainOrMalformedEmail_Returns404(string email)
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            new Uri($"/auth/oauth/discover?email={Uri.EscapeDataString(email)}", UriKind.Relative),
            Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Discover_IsAnonymousAndThrottledByTheAuthIpPolicy()
    {
        var discover = typeof(OAuthControllerBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.GetCustomAttributes<HttpGetAttribute>().Any(a => a.Template == "discover"));

        discover.GetCustomAttribute<AllowAnonymousAttribute>().Should().NotBeNull();
        discover.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName
            .Should().Be(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp);
    }

    [Fact]
    public void GenericProviderRoute_IsAnonymous()
    {
        var generic = typeof(OAuthControllerBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.GetCustomAttributes<HttpMethodAttribute>().Any(a => a.Template == "{provider}"));

        generic.GetCustomAttribute<AllowAnonymousAttribute>().Should().NotBeNull(
            "the challenge runs before any local token exists");
    }

    private sealed record DiscoveryBody(string Provider, string DisplayName);

    private sealed class RoutedOAuthControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Add(typeof(RoutedOAuthController).GetTypeInfo());
    }
}

// Internal so the default controller discovery of other test hosts in this assembly never picks it up;
// the host above adds it explicitly. Carries the class-level route the consumer's subclass declares.
[ApiController]
[Route("auth/oauth")]
internal sealed class RoutedOAuthController(
    IAuthenticationService authenticationService,
    ICacheService cacheService,
    IConfiguration configuration)
    : OAuthControllerBase(authenticationService, cacheService, configuration);
