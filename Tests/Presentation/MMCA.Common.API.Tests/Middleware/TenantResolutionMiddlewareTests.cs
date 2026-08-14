using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Middleware;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Settings;
using Moq;

namespace MMCA.Common.API.Tests.Middleware;

/// <summary>
/// Coverage for tenant resolution at the API edge: the configured strategy order, the fail-closed
/// rejection, the excluded paths, and the two shapes that must pass every request straight through
/// (tenancy disabled, and a host that never called <c>AddMultiTenancy</c>).
/// </summary>
public sealed class TenantResolutionMiddlewareTests
{
    private const string Acme = "acme";
    private const string Globex = "globex";

    [Fact]
    public async Task Disabled_PassesThroughWithoutResolving()
    {
        var tenantContext = new Mock<ITenantContext>();
        var context = HttpContextWith(header: Acme);

        var nextCalled = await InvokeAsync(context, tenantContext, new TenancySettings());

        nextCalled.Should().BeTrue();
        tenantContext.Verify(t => t.SetTenant(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NoOptionsRegistered_BehavesLikeDisabled()
    {
        // A host that never called AddMultiTenancy still resolves IOptions<TenancySettings> to the
        // framework defaults, which is what makes the unconditional pipeline wiring safe.
        var tenantContext = new Mock<ITenantContext>();
        var context = HttpContextWith(header: Acme);

        var nextCalled = await InvokeAsync(context, tenantContext, Options.Create(new TenancySettings()).Value);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        tenantContext.Verify(t => t.SetTenant(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Claim_WinsOverHeader_InTheDefaultOrder()
    {
        var tenantContext = new Mock<ITenantContext>();
        var context = HttpContextWith(header: Globex, claim: Acme);

        await InvokeAsync(context, tenantContext, Enabled());

        tenantContext.Verify(t => t.SetTenant(Acme), Times.Once,
            "the claim was signed by the issuer; the header is caller-supplied");
    }

    [Fact]
    public async Task Header_IsTheFallback_WhenNoClaimIsPresent()
    {
        var tenantContext = new Mock<ITenantContext>();
        var context = HttpContextWith(header: Globex);

        await InvokeAsync(context, tenantContext, Enabled());

        tenantContext.Verify(t => t.SetTenant(Globex), Times.Once);
    }

    [Fact]
    public async Task ConfiguredOrder_IsHonored()
    {
        var settings = Enabled();
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Header);
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Claim);

        var tenantContext = new Mock<ITenantContext>();
        var context = HttpContextWith(header: Globex, claim: Acme);

        await InvokeAsync(context, tenantContext, settings);

        tenantContext.Verify(t => t.SetTenant(Globex), Times.Once);
    }

    [Fact]
    public async Task CustomClaimTypeAndHeaderName_AreHonored()
    {
        var settings = new TenancySettings { Enabled = true, ClaimType = "org", HeaderName = "X-Org" };

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Org"] = Globex;

        var tenantContext = new Mock<ITenantContext>();
        await InvokeAsync(context, tenantContext, settings);

        tenantContext.Verify(t => t.SetTenant(Globex), Times.Once);
    }

    [Fact]
    public async Task ResolvedTenant_IsTrimmed()
    {
        var tenantContext = new Mock<ITenantContext>();
        var context = HttpContextWith(header: "  acme  ");

        await InvokeAsync(context, tenantContext, Enabled());

        tenantContext.Verify(t => t.SetTenant(Acme), Times.Once);
    }

    [Fact]
    public async Task BlankHeader_IsNotATenant()
    {
        var tenantContext = new Mock<ITenantContext>();
        var context = HttpContextWith(header: "   ");

        await InvokeAsync(context, tenantContext, Enabled());

        tenantContext.Verify(t => t.SetTenant(It.IsAny<string>()), Times.Never);
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task RequireTenant_RejectsAnUnresolvedRequest_AsProblemDetails()
    {
        var tenantContext = new Mock<ITenantContext>();
        var context = new DefaultHttpContext();
        ProblemDetailsContext? written = null;

        var nextCalled = await InvokeAsync(
            context, tenantContext, Enabled(), problem => written = problem);

        nextCalled.Should().BeFalse("failing closed means the request never reaches the application");
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        written.Should().NotBeNull();
        written!.ProblemDetails.Status.Should().Be(StatusCodes.Status400BadRequest);
        written.ProblemDetails.Title.Should().Be(TenantResolutionMiddleware.UnresolvedTenantTitle);
        written.ProblemDetails.Detail.Should().Contain("tenant_id").And.Contain("X-Tenant-Id");
    }

    [Fact]
    public async Task RequireTenantFalse_LetsAnUnresolvedRequestThrough()
    {
        var settings = new TenancySettings { Enabled = true, RequireTenant = false };

        var tenantContext = new Mock<ITenantContext>();
        var context = new DefaultHttpContext();

        var nextCalled = await InvokeAsync(context, tenantContext, settings);

        nextCalled.Should().BeTrue();
        tenantContext.Verify(t => t.SetTenant(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/alive")]
    [InlineData("/.well-known/jwks.json")]
    public async Task ExcludedPaths_PassThroughWithoutATenant(string path)
    {
        var tenantContext = new Mock<ITenantContext>();
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        var nextCalled = await InvokeAsync(context, tenantContext, Enabled());

        nextCalled.Should().BeTrue("probes and discovery documents must answer before any tenant exists");
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        tenantContext.Verify(t => t.SetTenant(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ConfiguredExclusions_ReplaceTheDefaults()
    {
        var settings = Enabled();
        settings.ExcludedPathPrefixes.Add("/ping");

        var tenantContext = new Mock<ITenantContext>();
        var health = new DefaultHttpContext();
        health.Request.Path = "/health";

        await InvokeAsync(health, tenantContext, settings);

        health.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest,
            "a configured exclusion list replaces the framework default rather than extending it");
    }

    [Fact]
    public async Task NonExcludedPath_WithATenant_ReachesTheApplication()
    {
        var tenantContext = new Mock<ITenantContext>();
        var context = HttpContextWith(header: Acme);
        context.Request.Path = "/api/tickets";

        var nextCalled = await InvokeAsync(context, tenantContext, Enabled());

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    // ── Scaffolding ──
    private static TenancySettings Enabled() => new() { Enabled = true };

    private static DefaultHttpContext HttpContextWith(string? header = null, string? claim = null)
    {
        var context = new DefaultHttpContext();

        if (header is not null)
        {
            context.Request.Headers["X-Tenant-Id"] = header;
        }

        if (claim is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", claim)], "test"));
        }

        return context;
    }

    private static async Task<bool> InvokeAsync(
        HttpContext context,
        Mock<ITenantContext> tenantContext,
        TenancySettings settings,
        Action<ProblemDetailsContext>? onProblem = null)
    {
        var nextCalled = false;
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService
            .Setup(s => s.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .Callback<ProblemDetailsContext>(ctx => onProblem?.Invoke(ctx))
            .Returns(new ValueTask<bool>(true));

        var sut = new TenantResolutionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(
            context,
            tenantContext.Object,
            Options.Create(settings),
            problemDetailsService.Object);

        return nextCalled;
    }
}
