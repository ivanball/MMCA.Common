using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Authorization.Fallback;

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

    private static FallbackAuthorizationHandler CreateHandler() =>
        new(Options.Create(new FallbackAuthorizationOptions()));

    private static AuthorizationHandlerContext CreateContext(string path, bool authenticated)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;

        var identity = authenticated
            ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "someone")], authenticationType: "TestAuth")
            : new ClaimsIdentity();

        var user = new ClaimsPrincipal(identity);
        httpContext.User = user;

        return new AuthorizationHandlerContext([new FallbackAuthorizationRequirement()], user, httpContext);
    }
}
