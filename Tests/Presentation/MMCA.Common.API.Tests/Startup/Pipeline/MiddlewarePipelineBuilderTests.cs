using AwesomeAssertions;
using MMCA.Common.API.Startup.Pipeline;

namespace MMCA.Common.API.Tests.Startup.Pipeline;

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
        MiddlewarePipelineStepNames.ViewAsGuard,
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
    [InlineData(nameof(MiddlewarePipelineBuilder.InsertBefore), "Nope", "anchor")]
    [InlineData(nameof(MiddlewarePipelineBuilder.InsertBefore), "", "anchor")]
    [InlineData(nameof(MiddlewarePipelineBuilder.InsertBefore), "   ", "anchor")]
    [InlineData(nameof(MiddlewarePipelineBuilder.InsertAfter), "Nope", "anchor")]
    [InlineData(nameof(MiddlewarePipelineBuilder.Replace), "Nope", "name")]
    [InlineData(nameof(MiddlewarePipelineBuilder.Remove), "Nope", "name")]
    public void Operation_WithUnknownTargetStep_Throws(string operation, string unknownTarget, string parameterName)
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        Action act = operation switch
        {
            nameof(MiddlewarePipelineBuilder.InsertBefore) => () => builder.InsertBefore(unknownTarget, Step("Tracing")),
            nameof(MiddlewarePipelineBuilder.InsertAfter) => () => builder.InsertAfter(unknownTarget, Step("Tracing")),
            nameof(MiddlewarePipelineBuilder.Replace) => () => builder.Replace(unknownTarget, Step("Tracing")),
            nameof(MiddlewarePipelineBuilder.Remove) => () => builder.Remove(unknownTarget),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

        act.Should().Throw<ArgumentException>().WithParameterName(parameterName);
    }

    // A step name already in the pipeline (Cors) is rejected whichever operation tries to add it.
    [Theory]
    [InlineData(nameof(MiddlewarePipelineBuilder.InsertBefore), MiddlewarePipelineStepNames.Routing)]
    [InlineData(nameof(MiddlewarePipelineBuilder.InsertAfter), MiddlewarePipelineStepNames.Routing)]
    [InlineData(nameof(MiddlewarePipelineBuilder.Replace), MiddlewarePipelineStepNames.OutputCache)]
    public void Operation_WithAnotherStepsName_Throws(string operation, string target)
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        var duplicate = Step(MiddlewarePipelineStepNames.Cors);
        Action act = operation switch
        {
            nameof(MiddlewarePipelineBuilder.InsertBefore) => () => builder.InsertBefore(target, duplicate),
            nameof(MiddlewarePipelineBuilder.InsertAfter) => () => builder.InsertAfter(target, duplicate),
            nameof(MiddlewarePipelineBuilder.Replace) => () => builder.Replace(target, duplicate),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

        act.Should().Throw<ArgumentException>().WithParameterName("step");
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
    [Theory]
    [InlineData(
        "IntruderBetweenPreForwardedCaptureAndForwardedHeaders",
        $"*'{MiddlewarePipelineStepNames.PreForwardedCapture}' must run immediately before '{MiddlewarePipelineStepNames.ForwardedHeaders}'*")]
    [InlineData(
        "IntruderBetweenAuthenticationAndTenantResolution",
        $"*'{MiddlewarePipelineStepNames.Authentication}' must run immediately before '{MiddlewarePipelineStepNames.TenantResolution}'*")]
    [InlineData(
        "AuthenticationAfterRateLimiting",
        $"*'{MiddlewarePipelineStepNames.Authentication}' must run before '{MiddlewarePipelineStepNames.RateLimiting}'*")]
    [InlineData(
        "HttpsRedirectionBeforeForwardedHeaders",
        $"*'{MiddlewarePipelineStepNames.ForwardedHeaders}' must run before '{MiddlewarePipelineStepNames.HttpsRedirection}'*")]
    public void Build_WhenAGuardedPairIsOutOfOrder_Throws(string scenario, string expectedMessage)
    {
        var builder = ArrangeGuardViolation(scenario);
        var build = () => builder.Build();

        build.Should().Throw<InvalidOperationException>().WithMessage(expectedMessage);
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

    private static MiddlewarePipelineBuilder ArrangeGuardViolation(string scenario) => scenario switch
    {
        "IntruderBetweenPreForwardedCaptureAndForwardedHeaders" => MiddlewarePipelineBuilder.CreateDefault()
            .InsertBefore(MiddlewarePipelineStepNames.ForwardedHeaders, Step("Intruder")),
        "IntruderBetweenAuthenticationAndTenantResolution" => MiddlewarePipelineBuilder.CreateDefault()
            .InsertAfter(MiddlewarePipelineStepNames.Authentication, Step("Intruder")),

        // Move authentication (and the tenant resolution that must stay glued to it) after the limiter.
        "AuthenticationAfterRateLimiting" => MiddlewarePipelineBuilder.CreateDefault()
            .Remove(MiddlewarePipelineStepNames.Authentication)
            .Remove(MiddlewarePipelineStepNames.TenantResolution)
            .InsertAfter(MiddlewarePipelineStepNames.RateLimiting, Step(MiddlewarePipelineStepNames.TenantResolution))
            .InsertBefore(MiddlewarePipelineStepNames.TenantResolution, Step(MiddlewarePipelineStepNames.Authentication)),

        // Move the HTTPS redirect ahead of the capture/forwarded-headers pair, keeping that pair adjacent.
        "HttpsRedirectionBeforeForwardedHeaders" => MiddlewarePipelineBuilder.CreateDefault()
            .Remove(MiddlewarePipelineStepNames.HttpsRedirection)
            .InsertBefore(MiddlewarePipelineStepNames.PreForwardedCapture, Step(MiddlewarePipelineStepNames.HttpsRedirection)),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario."),
    };

    private static MiddlewarePipelineStep Step(string name) => new(name, static _ => { });
}
