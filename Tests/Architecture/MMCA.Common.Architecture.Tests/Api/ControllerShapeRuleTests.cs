using MMCA.Common.Architecture.Tests.ControllerConventionFixtures;
using MMCA.Common.Architecture.Tests.RuleShapeFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Api;

/// <summary>
/// Adversarial coverage for <c>ControllersAreSealed</c> and <c>ControllersInheritApiControllerBase</c>,
/// which Store and ADC run over their API layers. Both are pointed at THIS assembly, whose
/// <c>RuleShapeFixtures</c> compile a controller by name only, by base type only, sealed and unsealed,
/// on the framework base and on the raw ASP.NET one; the failure must name every violating shape and
/// none of the compliant ones.
/// </summary>
public sealed class ControllerShapeRuleTests
{
    private static readonly Assembly Fixtures = typeof(ShapeOrdersController).Assembly;

    // -- ControllersAreSealed --
    [Fact]
    public void UnsealedControllers_ByBaseTypeOrByName_AreFlagged()
    {
        var act = () => ArchitectureRules.ControllersAreSealed(Map(Module(Layer.Api, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"  - {typeof(ShapeOpenOrdersController).FullName}");
        message.Should().Contain($"  - {typeof(ShapeOpenReportsEndpoint).FullName}", "a ControllerBase subclass is a controller whatever its name");
        message.Should().Contain($"  - {typeof(ShapeOpenPlainController).FullName}", "the Controller suffix alone makes a controller");
        message.Should().Contain("controllers must be sealed");
        message.Should().NotContain(nameof(ShapeOrdersController), "a sealed controller is compliant");
        message.Should().NotContain(nameof(ShapeOpenRouteHelper), "an unsealed type that is no controller is not read");
    }

    [Fact]
    public void UnsealedControllers_InAFrameworkApiLayer_AreFlaggedToo()
    {
        var act = () => ArchitectureRules.ControllersAreSealed(Map(Framework(Layer.Api, Fixtures)));

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(nameof(ShapeOpenOrdersController), "the rule reads framework and module API layers alike");
    }

    [Fact]
    public void ControllersOutsideTheApiLayer_AndTheFrameworkApi_Pass()
    {
        var act = () => ArchitectureRules.ControllersAreSealed(Map(
            Module(Layer.Application, Fixtures),
            Framework(Layer.Api, typeof(Common.API.Controllers.ApiControllerBase).Assembly)));

        act.Should().NotThrow("only API layers are read, and every concrete framework controller is sealed");
    }

    // -- ControllersInheritApiControllerBase --
    [Fact]
    public void ControllerOnTheRawBase_IsFlagged_UnlessExempt()
    {
        var act = () => ArchitectureRules.ControllersInheritApiControllerBase(Map(Module(Layer.Api, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"  - {typeof(ShapeRawController).FullName}");
        message.Should().Contain($"  - {typeof(ShapeWebhookController).FullName}", "nothing is exempt by default");
        message.Should().Contain("controllers must inherit ApiControllerBase/EntityControllerBase");
        message.Should().NotContain($"{typeof(ShapeOrdersController).FullName}", "ApiControllerBase is the expected base");
        message.Should().NotContain(
            nameof(BodyReadOverrideFixtureController),
            "EntityControllerBase is an accepted base as well");
    }

    [Fact]
    public void ExemptController_IsNotFlagged()
    {
        var act = () => ArchitectureRules.ControllersInheritApiControllerBase(
            Map(Module(Layer.Api, Fixtures)),
            [typeof(ShapeWebhookController).FullName!]);

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().NotContain(nameof(ShapeWebhookController), "a controller listed by full name owns its own response semantics");
        message.Should().Contain(nameof(ShapeRawController), "the exemption is per type, not a blanket pass");
    }

    [Fact]
    public void ControllerBase_IgnoresFrameworkApiLayers()
    {
        var act = () => ArchitectureRules.ControllersInheritApiControllerBase(Map(Framework(Layer.Api, Fixtures)));

        act.Should().NotThrow("the rule reads module API layers only");
    }

    private static FixtureMap Map(params LayerRef[] layers) => new(layers);

    private static LayerRef Module(Layer layer, Assembly assembly) =>
        new("Shapes", layer, assembly, $"MMCA.Common.Shapes.{layer}");

    private static LayerRef Framework(Layer layer, Assembly assembly) =>
        new(string.Empty, layer, assembly, $"MMCA.Common.{layer}");

    /// <summary>A map over exactly the layers a test declares.</summary>
    private sealed class FixtureMap(LayerRef[] layers) : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() => layers;
    }
}
