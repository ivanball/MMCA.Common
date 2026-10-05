using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Asp.Versioning;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Caching;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.Authorization;

/// <summary>
/// X-22 (Store run 1): with the deny-by-default fallback policy registered, an anonymous request that
/// names an api-version the route does not support is answered 401, because the versioning library's
/// rejection endpoint carries no authorization metadata and the fallback challenges it. The caller
/// sent a malformed request, not an unauthenticated one: it must get the 400 UnsupportedApiVersion the
/// versioning library produces, while a protected endpoint at a supported version still challenges
/// with 401. Output-cache hits must keep the <c>api-supported-versions</c> header the first response
/// carried (ReportApiVersions is on in <c>AddCommonApiVersioning</c>).
/// </summary>
public sealed class ApiVersionRejectionAuthorizationTests
{
    private const string ApiVersionHeader = "api-version";
    private const string SupportedVersionsHeader = "api-supported-versions";

    [Fact]
    public async Task AnonymousRequest_WithAnUnsupportedApiVersion_Answers400UnsupportedApiVersion()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/versioned-probe/public", UriKind.Relative));
        request.Headers.Add(ApiVersionHeader, "9.0");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "an unsupported api-version is a malformed request; challenging it hides the real error behind a sign-in prompt");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("UnsupportedApiVersion");
    }

    [Fact]
    public async Task AnonymousRequest_ToAProtectedEndpoint_WithAnUnsupportedApiVersion_Answers400()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/versioned-protected", UriKind.Relative));
        request.Headers.Add(ApiVersionHeader, "9.0");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "no endpoint at that version exists, so there is nothing to authorize; the version error is the answer");
    }

    // Sanity baseline: the versioning library's own answer, as seen by an authenticated caller who
    // never meets the fallback challenge. The anonymous case above must match it.
    [Fact]
    public async Task AuthenticatedRequest_WithAnUnsupportedApiVersion_Answers400UnsupportedApiVersion()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/versioned-probe/public", UriKind.Relative));
        request.Headers.Add(ApiVersionHeader, "9.0");
        request.Headers.Add(HeaderAuthenticationHandler.UserHeader, "someone");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("UnsupportedApiVersion");
    }

    [Fact]
    public async Task AnonymousRequest_ToAProtectedEndpoint_AtASupportedVersion_StillAnswers401()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/versioned-protected", UriKind.Relative));
        request.Headers.Add(ApiVersionHeader, "1.0");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the fix must not open protected endpoints");
    }

    [Fact]
    public async Task AnonymousRequest_ToAnUndecoratedEndpoint_AtASupportedVersion_StillAnswers401()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/versioned-undecorated", UriKind.Relative));
        request.Headers.Add(ApiVersionHeader, "1.0");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "an endpoint that forgot [Authorize] is exactly what the fallback policy exists to close");
    }

    [Fact]
    public async Task OutputCacheHit_KeepsTheApiSupportedVersionsHeader()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();
        var counter = app.Services.GetRequiredService<ProbeInvocationCounter>();

        using var first = await client.GetAsync(new Uri("/versioned-probe/cached", UriKind.Relative), TestContext.Current.CancellationToken);
        using var second = await client.GetAsync(new Uri("/versioned-probe/cached", UriKind.Relative), TestContext.Current.CancellationToken);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        counter.Count.Should().Be(1, "the second request is served from the output cache (precondition)");
        first.Headers.Contains(SupportedVersionsHeader).Should().BeTrue("ReportApiVersions stamps the live response (precondition)");
        second.Headers.Contains(SupportedVersionsHeader).Should().BeTrue(
            "a cached response must carry the same api-supported-versions header as the response it was stored from");
    }

    private static async Task<WebApplication> CreateHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<ProbeInvocationCounter>();

        // Every service host has a problem-details writer (AddCommonExceptionHandlers registers one),
        // which is what the versioning library writes its UnsupportedApiVersion body through.
        builder.Services.AddProblemDetails();
        builder.Services
            .AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorizationPolicies();
        builder.Services.AddOutputCache(options => options.AddPublicEndpointPolicy(
            ApiVersionProbeController.CachePolicy, TimeSpan.FromMinutes(5), "probe"));
        builder.Services.AddCommonApiVersioning();
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (var i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                    manager.FeatureProviders.RemoveAt(i);
            }

            manager.FeatureProviders.Add(new ProbeControllerFeatureProvider());
        });

        var app = builder.Build();

        // Same relative order as the framework's MiddlewarePipelineBuilder.
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseOutputCache();
        app.MapControllers();
        await app.StartAsync();

        return app;
    }

    private sealed class ProbeControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Add(typeof(ApiVersionProbeController).GetTypeInfo());
            feature.Controllers.Add(typeof(ApiVersionProtectedProbeController).GetTypeInfo());
            feature.Controllers.Add(typeof(ApiVersionUndecoratedProbeController).GetTypeInfo());
        }
    }

    /// <summary>Authenticates a request carrying <see cref="UserHeader"/>; everything else is anonymous.</summary>
    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "HeaderTest";
        public const string UserHeader = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrEmpty(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

/// <summary>Counts how many times the cached probe action actually ran.</summary>
public sealed class ProbeInvocationCounter
{
    private int _count;

    public int Count => _count;

    public int Increment() => Interlocked.Increment(ref _count);
}

/// <summary>An anonymous versioned controller: one plain action and one output-cached action.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("versioned-probe")]
[AllowAnonymous]
public sealed class ApiVersionProbeController(ProbeInvocationCounter counter) : ControllerBase
{
    public const string CachePolicy = "ProbeCache";

    [HttpGet("public")]
    public ActionResult<string> Public() => "public";

    [HttpGet("cached")]
    [OutputCache(PolicyName = CachePolicy)]
    public ActionResult<int> Cached() => counter.Increment();
}

/// <summary>A versioned controller that requires an authenticated caller.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("versioned-protected")]
[Authorize]
public sealed class ApiVersionProtectedProbeController : ControllerBase
{
    [HttpGet]
    public ActionResult<string> Get() => "protected";
}

/// <summary>A versioned controller that declares no authorization at all (the fallback policy's case).</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("versioned-undecorated")]
public sealed class ApiVersionUndecoratedProbeController : ControllerBase
{
    [HttpGet]
    public ActionResult<string> Get() => "undecorated";
}
