using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Authorization.Fallback;
using Moq;

namespace MMCA.Common.API.Tests.Authorization;

/// <summary>
/// The insecure default this closes (SEC-Common-16): with no fallback policy, a controller that
/// forgets <c>[Authorize]</c> publishes every inherited action to anonymous callers and no fitness
/// test can see the omission. These pin that the framework now registers a fallback, that the
/// documented opt-out really turns it off, and that the exempt surfaces (Blazor framework files,
/// static asset roots, probes) still answer anonymously so nothing legitimate breaks.
/// </summary>
public sealed class FallbackAuthorizationTests
{
    [Fact]
    public void AddAuthorizationPolicies_RegistersAFallbackPolicyByDefault()
    {
        var services = new ServiceCollection();
        services.AddAuthorizationPolicies();

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<AuthorizationOptions>>();

        options.Value.FallbackPolicy.Should().NotBeNull(
            "an endpoint that declares no authorization must fail closed, not answer anonymously");
        options.Value.FallbackPolicy!.Requirements.Should().ContainSingle(r => r is FallbackAuthorizationRequirement);
    }

    [Fact]
    public void AddAuthorizationPolicies_WithTheDocumentedOptOut_RegistersNoFallbackPolicy()
    {
        var services = new ServiceCollection();
        services.AddAuthorizationPolicies(options => options.Enabled = false);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<AuthorizationOptions>>();

        options.Value.FallbackPolicy.Should().BeNull(
            "the opt-out has to restore the previous behaviour exactly, or it is not an escape hatch");
    }

    [Fact]
    public void AddAuthorizationPolicies_RegistersTheFallbackHandler()
    {
        var services = new ServiceCollection();
        services.AddAuthorizationPolicies();

        services.BuildServiceProvider()
            .GetServices<IAuthorizationHandler>()
            .Should().ContainSingle(h => h is FallbackAuthorizationHandler);
    }

    [Fact]
    public async Task Handler_DeniesAnAnonymousCallerOnAnOrdinaryPath()
    {
        var context = CreateContext("/products", authenticated: false);

        await CreateHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse(
            "this is the forgotten-[Authorize] case the fallback exists for");
    }

    [Fact]
    public async Task Handler_GrantsAnAuthenticatedCaller()
    {
        var context = CreateContext("/products", authenticated: true);

        await CreateHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_blazor")]
    [InlineData("/_content/MMCA.Common.UI/app.css")]
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/.well-known/jwks.json")]
    [InlineData("/favicon.ico")]
    [InlineData("/CSS/site.css")]
    public async Task Handler_GrantsTheFrameworkAndStaticSurfacesAnonymously(string path)
    {
        var context = CreateContext(path, authenticated: false);

        await CreateHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeTrue(
            "a Blazor host maps these as endpoints with no authorization metadata of their own, so "
            + "gating them would break the page that has to render the sign-in form");
    }

    [Fact]
    public async Task Handler_DeniesAPathThatMerelyStartsWithAnExemptPrefix()
    {
        var context = CreateContext("/healthcheck-admin", authenticated: false);

        await CreateHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse("matching is segment-based, not a raw string prefix");
    }

    [Fact]
    public async Task Handler_DeniesANonHttpResourceForAnAnonymousCaller()
    {
        var context = new AuthorizationHandlerContext(
            [new FallbackAuthorizationRequirement()],
            new ClaimsPrincipal(new ClaimsIdentity()),
            resource: new object());

        await CreateHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse("with no request path there is no exemption to claim");
    }

    // -- An unknown URL reaches the 404 and the not-found page (U-41) --
    [Fact]
    public async Task Handler_GrantsAnAnonymousRequestThatMatchedNoEndpoint()
    {
        var context = CreateContext("/no-such-page", authenticated: false, matchedEndpoint: false);

        await CreateHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeTrue(
            "nothing is behind an unmatched URL, and a challenge would hide the host's 404 page");
    }

    [Fact]
    public async Task Handler_StillDeniesAnUnmatchedRequestForAFileInTheWebRoot()
    {
        // Static-file middleware after authorization serves this file without an endpoint, so it
        // keeps the gate it always had.
        var file = new Mock<IFileInfo>();
        file.SetupGet(f => f.Exists).Returns(true);
        var webRoot = new Mock<IFileProvider>();
        webRoot.Setup(p => p.GetFileInfo("/reports/private.pdf")).Returns(file.Object);
        webRoot.Setup(p => p.GetDirectoryContents(It.IsAny<string>())).Returns(NotFoundDirectoryContents.Singleton);
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.WebRootFileProvider).Returns(webRoot.Object);
        var services = new ServiceCollection().AddSingleton(environment.Object).BuildServiceProvider();

        var context = CreateContext("/reports/private.pdf", authenticated: false, matchedEndpoint: false, services);

        await CreateHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Pipeline_AnonymousUnknownUrl_RendersTheNotFoundPageInsteadOfRedirectingToLogin()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        using var unknown = await client.GetAsync(new Uri("/no-such-page", UriKind.Relative), TestContext.Current.CancellationToken);
        using var matched = await client.GetAsync(new Uri("/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("Page Not Found");
        matched.StatusCode.Should().Be(HttpStatusCode.Redirect, "an endpoint with no authorization metadata is still challenged");
        matched.Headers.Location!.OriginalString.Should().Contain("/login");
    }

    private static async Task<WebApplication> CreateHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.LoginPath = "/login");
        builder.Services.AddAuthorizationPolicies();

        var app = builder.Build();
        app.UseStatusCodePagesWithReExecute("/not-found");
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/not-found", () => Results.Text("Page Not Found")).AllowAnonymous();
        app.MapGet("/dashboard", () => Results.Text("private"));
        await app.StartAsync();

        return app;
    }

    private static FallbackAuthorizationHandler CreateHandler() =>
        new(Options.Create(new FallbackAuthorizationOptions()));

    // matchedEndpoint: true models what the policy was written for, an endpoint routing matched that
    // carries no authorization metadata of its own.
    private static AuthorizationHandlerContext CreateContext(
        string path,
        bool authenticated,
        bool matchedEndpoint = true,
        IServiceProvider? requestServices = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        if (matchedEndpoint)
        {
            httpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, path));
        }

        if (requestServices is not null)
        {
            httpContext.RequestServices = requestServices;
        }

        var identity = authenticated
            ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "someone")], authenticationType: "TestAuth")
            : new ClaimsIdentity();

        var user = new ClaimsPrincipal(identity);
        httpContext.User = user;

        return new AuthorizationHandlerContext([new FallbackAuthorizationRequirement()], user, httpContext);
    }
}
