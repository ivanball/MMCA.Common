using AwesomeAssertions;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// Unit tests for the named-step edge pipeline builder: the default order, the insert/replace/remove
/// operations a host uses to customize it, and the load-bearing invariants
/// <see cref="MiddlewarePipelineBuilder.Build"/> re-checks so a customized pipeline fails at startup
/// rather than misrouting at runtime.
/// </summary>
public sealed class MiddlewarePipelineBuilderTests
{
    private static readonly string[] DefaultOrder =
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

    // ── Defaults ──
    [Fact]
    public void CreateDefault_SeedsTheDocumentedStepOrder() =>
        MiddlewarePipelineBuilder.CreateDefault().StepNames.Should().Equal(DefaultOrder);

    [Fact]
    public void CreateDefault_BuildsWithoutViolatingAnyInvariant() =>
        MiddlewarePipelineBuilder.CreateDefault().Build().Select(step => step.Name).Should().Equal(DefaultOrder);

    [Fact]
    public void CreateDefault_ReturnsAnIndependentBuilderEachCall()
    {
        var first = MiddlewarePipelineBuilder.CreateDefault().Remove(MiddlewarePipelineStepNames.OutputCache);

        first.StepNames.Should().NotContain(MiddlewarePipelineStepNames.OutputCache);
        MiddlewarePipelineBuilder.CreateDefault().StepNames.Should().Contain(MiddlewarePipelineStepNames.OutputCache);
    }

    // ── Mutation operations ──
    [Fact]
    public void InsertBefore_PlacesTheStepImmediatelyBeforeTheAnchor()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault()
            .InsertBefore(MiddlewarePipelineStepNames.Routing, Step("Tracing"));

        builder.StepNames.Should().ContainInConsecutiveOrder(
            MiddlewarePipelineStepNames.ResponseCompression, "Tracing", MiddlewarePipelineStepNames.Routing);
    }

    [Fact]
    public void InsertAfter_PlacesTheStepImmediatelyAfterTheAnchor()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault()
            .InsertAfter(MiddlewarePipelineStepNames.Routing, Step("Tracing"));

        builder.StepNames.Should().ContainInConsecutiveOrder(
            MiddlewarePipelineStepNames.Routing, "Tracing", MiddlewarePipelineStepNames.Cors);
    }

    [Fact]
    public void Replace_SwapsTheStepAndKeepsItsPosition()
    {
        var replaced = false;
        var builder = MiddlewarePipelineBuilder.CreateDefault()
            .Replace(MiddlewarePipelineStepNames.ResponseCompression, new MiddlewarePipelineStep("BrotliOnly", _ => replaced = true));

        builder.StepNames.Should().ContainInConsecutiveOrder(
            MiddlewarePipelineStepNames.HttpsRedirection, "BrotliOnly", MiddlewarePipelineStepNames.Routing);
        builder.StepNames.Should().NotContain(MiddlewarePipelineStepNames.ResponseCompression);
        replaced.Should().BeFalse(because: "the step delegate runs only when the pipeline is applied to an application");
    }

    [Fact]
    public void Replace_KeepingTheSameName_IsAllowed()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault()
            .Replace(MiddlewarePipelineStepNames.OutputCache, Step(MiddlewarePipelineStepNames.OutputCache));

        builder.StepNames.Should().Equal(DefaultOrder);
    }

    [Fact]
    public void Remove_DropsTheStep()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault().Remove(MiddlewarePipelineStepNames.OutputCache);

        builder.StepNames.Should().NotContain(MiddlewarePipelineStepNames.OutputCache);
        builder.StepNames.Should().HaveCount(DefaultOrder.Length - 1);
    }

    [Fact]
    public void MutationOperations_ReturnTheSameBuilder_ForChaining()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();

        builder.InsertAfter(MiddlewarePipelineStepNames.Routing, Step("Tracing")).Should().BeSameAs(builder);
        builder.Remove("Tracing").Should().BeSameAs(builder);
    }

    // ── Unknown anchors and duplicate names ──
    [Theory]
    [InlineData("Nope")]
    [InlineData("")]
    [InlineData("   ")]
    public void InsertBefore_WithUnknownAnchor_Throws(string unknownAnchor)
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        var insert = () => builder.InsertBefore(unknownAnchor, Step("Tracing"));

        insert.Should().Throw<ArgumentException>().WithParameterName("anchor");
    }

    [Fact]
    public void InsertAfter_WithUnknownAnchor_Throws()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        var insert = () => builder.InsertAfter("Nope", Step("Tracing"));

        insert.Should().Throw<ArgumentException>().WithParameterName("anchor");
    }

    [Fact]
    public void Replace_WithUnknownName_Throws()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        var replace = () => builder.Replace("Nope", Step("Tracing"));

        replace.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void Remove_WithUnknownName_Throws()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        var remove = () => builder.Remove("Nope");

        remove.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void InsertBefore_WithDuplicateStepName_Throws()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        var insert = () => builder.InsertBefore(
            MiddlewarePipelineStepNames.Routing, Step(MiddlewarePipelineStepNames.Cors));

        insert.Should().Throw<ArgumentException>().WithParameterName("step");
    }

    [Fact]
    public void InsertAfter_WithDuplicateStepName_Throws()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        var insert = () => builder.InsertAfter(
            MiddlewarePipelineStepNames.Routing, Step(MiddlewarePipelineStepNames.Cors));

        insert.Should().Throw<ArgumentException>().WithParameterName("step");
    }

    [Fact]
    public void Replace_WithAnotherStepsName_Throws()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        var replace = () => builder.Replace(
            MiddlewarePipelineStepNames.OutputCache, Step(MiddlewarePipelineStepNames.Cors));

        replace.Should().Throw<ArgumentException>().WithParameterName("step");
    }

    [Fact]
    public void MiddlewarePipelineStep_RejectsABlankNameAndANullDelegate()
    {
        var blankName = () => new MiddlewarePipelineStep(" ", static _ => { });
        var nullConfigure = () => new MiddlewarePipelineStep("Tracing", null!);

        blankName.Should().Throw<ArgumentException>();
        nullConfigure.Should().Throw<ArgumentNullException>();
    }

    // ── Invariants ──
    [Fact]
    public void Build_WhenPreForwardedCaptureIsNotImmediatelyBeforeForwardedHeaders_Throws()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault()
            .InsertBefore(MiddlewarePipelineStepNames.ForwardedHeaders, Step("Intruder"));
        var build = () => builder.Build();

        build.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{MiddlewarePipelineStepNames.PreForwardedCapture}' must run immediately before '{MiddlewarePipelineStepNames.ForwardedHeaders}'*");
    }

    [Fact]
    public void Build_WhenAuthenticationIsNotImmediatelyBeforeTenantResolution_Throws()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault()
            .InsertAfter(MiddlewarePipelineStepNames.Authentication, Step("Intruder"));
        var build = () => builder.Build();

        build.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{MiddlewarePipelineStepNames.Authentication}' must run immediately before '{MiddlewarePipelineStepNames.TenantResolution}'*");
    }

    [Fact]
    public void Build_WhenAuthenticationDoesNotPrecedeRateLimiting_Throws()
    {
        // Move authentication (and the tenant resolution that must stay glued to it) after the limiter.
        var builder = MiddlewarePipelineBuilder.CreateDefault()
            .Remove(MiddlewarePipelineStepNames.Authentication)
            .Remove(MiddlewarePipelineStepNames.TenantResolution)
            .InsertAfter(MiddlewarePipelineStepNames.RateLimiting, Step(MiddlewarePipelineStepNames.TenantResolution))
            .InsertBefore(MiddlewarePipelineStepNames.TenantResolution, Step(MiddlewarePipelineStepNames.Authentication));
        var build = () => builder.Build();

        build.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{MiddlewarePipelineStepNames.Authentication}' must run before '{MiddlewarePipelineStepNames.RateLimiting}'*");
    }

    [Fact]
    public void Build_WhenForwardedHeadersDoesNotPrecedeHttpsRedirection_Throws()
    {
        // Move the HTTPS redirect ahead of the capture/forwarded-headers pair, keeping that pair adjacent.
        var builder = MiddlewarePipelineBuilder.CreateDefault()
            .Remove(MiddlewarePipelineStepNames.HttpsRedirection)
            .InsertBefore(MiddlewarePipelineStepNames.PreForwardedCapture, Step(MiddlewarePipelineStepNames.HttpsRedirection));
        var build = () => builder.Build();

        build.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{MiddlewarePipelineStepNames.ForwardedHeaders}' must run before '{MiddlewarePipelineStepNames.HttpsRedirection}'*");
    }

    [Fact]
    public void Build_WhenBothMembersOfAGuardedPairAreRemoved_Succeeds()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault()
            .Remove(MiddlewarePipelineStepNames.PreForwardedCapture)
            .Remove(MiddlewarePipelineStepNames.ForwardedHeaders)
            .Remove(MiddlewarePipelineStepNames.Authentication)
            .Remove(MiddlewarePipelineStepNames.TenantResolution)
            .Remove(MiddlewarePipelineStepNames.RateLimiting);
        var build = () => builder.Build();

        build.Should().NotThrow(
            because: "an invariant binds only when both of the steps it names are present, so dropping a whole capability stays legal");
    }

    [Fact]
    public void Build_ReturnsAnImmutableSnapshotOfTheSteps()
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        var steps = builder.Build();

        builder.Remove(MiddlewarePipelineStepNames.OutputCache);

        steps.Select(step => step.Name).Should().Equal(DefaultOrder);
    }

    private static MiddlewarePipelineStep Step(string name) => new(name, static _ => { });
}
