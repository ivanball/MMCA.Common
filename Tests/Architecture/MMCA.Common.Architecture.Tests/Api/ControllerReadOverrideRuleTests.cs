using MMCA.Common.Architecture.Tests.ControllerConventionFixtures;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Api;

/// <summary>
/// Adversarial coverage for <c>ReadOverridesDoNotRedeclarePublicCaching</c> and its fact on
/// <see cref="ControllerConventionTestsBase"/>: an override of a base read action that carries
/// <c>[AllowAnonymous]</c> or <c>[OutputCache]</c> fails, naming <c>{Type}.{Method}</c>; an override with
/// a body and neither marker, and an app-declared action with both, pass. The fact scans module API
/// layers only. The convention subclasses are private so xUnit does not collect their inherited facts
/// as (deliberately failing) tests of their own.
/// </summary>
public sealed class ControllerReadOverrideRuleTests
{
    [Fact]
    public void OverrideWithAllowAnonymous_Fails()
    {
        var act = () => ArchitectureRules.ReadOverridesDoNotRedeclarePublicCaching(
            [typeof(AllowAnonymousReadOverrideFixtureController)]);

        act.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                $"{nameof(AllowAnonymousReadOverrideFixtureController)}.{nameof(AllowAnonymousReadOverrideFixtureController.GetByIdAsync)}",
                "the offender message must name the type and the overriding method");
    }

    [Fact]
    public void OverrideWithOutputCache_Fails()
    {
        var act = () => ArchitectureRules.ReadOverridesDoNotRedeclarePublicCaching(
            [typeof(OutputCacheReadOverrideFixtureController)]);

        act.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                $"{nameof(OutputCacheReadOverrideFixtureController)}.{nameof(OutputCacheReadOverrideFixtureController.GetAllAsync)}");
    }

    [Fact]
    public void OverrideWithBodyAndNoMarkers_Passes()
    {
        var act = () => ArchitectureRules.ReadOverridesDoNotRedeclarePublicCaching(
            [typeof(BodyReadOverrideFixtureController)]);

        act.Should().NotThrow("an override with real behaviour and no marker is what the rule leaves alone");
    }

    [Fact]
    public void AppActionWithMarkers_Passes()
    {
        var act = () => ArchitectureRules.ReadOverridesDoNotRedeclarePublicCaching(
            [typeof(AppActionMarkersFixtureController)]);

        act.Should().NotThrow("an app-declared action is not a base read, so it decides its own caching and access");
    }

    [Fact]
    public void EveryOffender_IsNamedInOneFailure()
    {
        var act = () => ArchitectureRules.ReadOverridesDoNotRedeclarePublicCaching(
        [
            typeof(AllowAnonymousReadOverrideFixtureController),
            typeof(OutputCacheReadOverrideFixtureController),
            typeof(BodyReadOverrideFixtureController),
            typeof(AppActionMarkersFixtureController),
        ]);

        var message = act.Should().Throw<Exception>().Which.Message;

        message.Should().Contain($"{nameof(AllowAnonymousReadOverrideFixtureController)}.GetByIdAsync");
        message.Should().Contain($"{nameof(OutputCacheReadOverrideFixtureController)}.GetAllAsync");
        message.Should().NotContain(nameof(BodyReadOverrideFixtureController));
        message.Should().NotContain(nameof(AppActionMarkersFixtureController));
    }

    // -- Rule 13: the fact runs over module API layers only --
    [Fact]
    public void Fact_ScansModuleApiLayers()
    {
        var assert = new ModuleLayerTests().ReadOverrides_ShouldNotRedeclare_PublicCaching;

        var message = assert.Should().Throw<Exception>().Which.Message;

        message.Should().Contain(nameof(AllowAnonymousReadOverrideFixtureController));
        message.Should().Contain(nameof(OutputCacheReadOverrideFixtureController));
    }

    [Fact]
    public void Fact_IgnoresFrameworkApiLayers()
    {
        var assert = new FrameworkLayerTests().ReadOverrides_ShouldNotRedeclare_PublicCaching;

        assert.Should().NotThrow("a framework API layer (Module empty) is outside the rule");
    }

    /// <summary>Declares this assembly, where the fixtures compile, as a module's API layer.</summary>
    private sealed class ModuleApiMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Module("Fixtures", Layer.Api, typeof(AllowAnonymousReadOverrideFixtureController).Assembly)];
    }

    /// <summary>Declares the same assembly as a framework API layer.</summary>
    private sealed class FrameworkApiMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Framework(Layer.Api, typeof(AllowAnonymousReadOverrideFixtureController).Assembly)];
    }

    private sealed class ModuleLayerTests : ControllerConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = new ModuleApiMap();
    }

    private sealed class FrameworkLayerTests : ControllerConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = new FrameworkApiMap();
    }
}
