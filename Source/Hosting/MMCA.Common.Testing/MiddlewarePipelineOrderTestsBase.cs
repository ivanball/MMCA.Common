using AwesomeAssertions;
using MMCA.Common.API.Startup;
using Xunit;

namespace MMCA.Common.Testing;

/// <summary>
/// Opt-in fitness function for the shared HTTP edge pipeline that <c>UseCommonMiddlewarePipeline</c>
/// applies: seeds <see cref="MiddlewarePipelineBuilder.CreateDefault"/>, applies the host's own
/// <see cref="Configure"/> customization if it has one, and asserts the resulting step order is
/// exactly the documented pipeline. It is the edge counterpart of
/// <see cref="DecoratorPipelineOrderTestsBase{TCommand, TCommandResult, TQuery, TQueryResult}"/>:
/// several adjacencies are load-bearing (pre-forwarded capture immediately before
/// <c>UseForwardedHeaders</c>, authentication immediately before tenant resolution, authentication
/// before the rate limiter per ADR-019, forwarded headers before the HTTPS redirect), and a reorder
/// that breaks one of them fails at runtime in ways that look like configuration bugs: an
/// unreachable <c>jwks_uri</c>, a tenant that never resolves, a per-user rate cap that never
/// engages. This base turns each of those into a test failure instead.
/// <para>
/// Subclass it with an empty body for a host that calls the zero-argument overload. A host that
/// customizes the pipeline overrides <see cref="Configure"/> with the same delegate its
/// <c>Program.cs</c> passes, and <see cref="ExpectedStepNames"/> with the order it expects.
/// </para>
/// <para>
/// No <c>WebApplication</c> is built: the steps are pure data until they are applied, so this runs
/// in the fast unit tier with no database and no host.
/// </para>
/// </summary>
public abstract class MiddlewarePipelineOrderTestsBase
{
    /// <summary>
    /// The host's pipeline customization, or null when it calls the zero-argument
    /// <c>UseCommonMiddlewarePipeline()</c> overload (the default).
    /// </summary>
    protected virtual Action<MiddlewarePipelineBuilder>? Configure => null;

    /// <summary>The expected step order, outermost first (the framework default pipeline).</summary>
    protected virtual IReadOnlyList<string> ExpectedStepNames =>
    [
        MiddlewarePipelineStepNames.ExceptionHandler,
        MiddlewarePipelineStepNames.CorrelationId,
        MiddlewarePipelineStepNames.RequestLocalization,
        MiddlewarePipelineStepNames.PreForwardedCapture,
        MiddlewarePipelineStepNames.ForwardedHeaders,
        MiddlewarePipelineStepNames.HttpsRedirection,
        MiddlewarePipelineStepNames.ResponseCompression,
        MiddlewarePipelineStepNames.Routing,
        MiddlewarePipelineStepNames.Cors,
        MiddlewarePipelineStepNames.Authentication,
        MiddlewarePipelineStepNames.TenantResolution,
        MiddlewarePipelineStepNames.RateLimiting,
        MiddlewarePipelineStepNames.SoftDeletedUserFilter,
        MiddlewarePipelineStepNames.Authorization,
        MiddlewarePipelineStepNames.OutputCache,
        MiddlewarePipelineStepNames.JwksEndpoint,
        MiddlewarePipelineStepNames.OidcDiscoveryEndpoint,
        MiddlewarePipelineStepNames.Controllers,
    ];

    [Fact]
    public void EdgePipeline_OrdersSteps_InDocumentedOrder()
    {
        var builder = CreateBuilder();

        builder.StepNames.Should().Equal(ExpectedStepNames,
            because: "the edge pipeline must run in exactly the documented order, outermost first: the pre-forwarded capture has to sit immediately before UseForwardedHeaders for jwks_uri to stay reachable, tenant resolution immediately after authentication so the claim strategy sees HttpContext.User, and the rate limiter after authentication per ADR-019 so the per-user cap engages");
    }

    [Fact]
    public void EdgePipeline_SatisfiesLoadBearingInvariants()
    {
        var builder = CreateBuilder();
        var build = () => builder.Build();

        build.Should().NotThrow(
            because: "Build() re-checks the load-bearing adjacencies at startup, so a pipeline that fails here would have thrown while the host was starting");
    }

    private MiddlewarePipelineBuilder CreateBuilder()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        Configure?.Invoke(builder);
        return builder;
    }
}
