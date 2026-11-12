using MMCA.Common.Architecture.Tests.ControllerConventionFixtures;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Api;

/// <summary>
/// Adversarial coverage for <c>ConditionalActionsDoNotRedeclarePreconditionResponses</c> and its fact on
/// <see cref="ControllerConventionTestsBase"/>: an action carrying <c>[SupportsIfMatch]</c> that also
/// declares 412 or 428 by hand (typed, untyped or through the generic attribute) fails, naming
/// <c>{Type}.{Method}</c>; a conditional action declaring neither, and a non-conditional action
/// declaring 412, pass. The fact scans module API layers only, so the framework's own
/// <c>CrudEntityControllerBase.UpdateAsync</c> keeps its explicit stack. The convention subclasses are
/// private so xUnit does not collect their inherited facts.
/// </summary>
public sealed class ConditionalActionResponseRuleTests
{
    [Fact]
    public void ConditionalActionDeclaring412_Fails()
    {
        var act = () => ArchitectureRules.ConditionalActionsDoNotRedeclarePreconditionResponses(
            [typeof(Conditional412FixtureController)]);

        act.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                $"{nameof(Conditional412FixtureController)}.{nameof(Conditional412FixtureController.Declares412)}",
                "the offender message must name the type and the action");
    }

    [Fact]
    public void ConditionalActionDeclaring428_Fails()
    {
        var act = () => ArchitectureRules.ConditionalActionsDoNotRedeclarePreconditionResponses(
            [typeof(Conditional428FixtureController)]);

        act.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                $"{nameof(Conditional428FixtureController)}.{nameof(Conditional428FixtureController.Declares428)}");
    }

    [Fact]
    public void ConditionalActionDeclaringGeneric412_Fails()
    {
        var act = () => ArchitectureRules.ConditionalActionsDoNotRedeclarePreconditionResponses(
            [typeof(ConditionalGeneric412FixtureController)]);

        act.Should().Throw<Exception>(
                "ProducesResponseType<T> derives from ProducesResponseTypeAttribute, so its 412 counts")
            .Which.Message.Should().Contain(
                $"{nameof(ConditionalGeneric412FixtureController)}.{nameof(ConditionalGeneric412FixtureController.DeclaresGeneric412)}");
    }

    [Fact]
    public void ConditionalActionWithoutPreconditionResponses_Passes()
    {
        var act = () => ArchitectureRules.ConditionalActionsDoNotRedeclarePreconditionResponses(
            [typeof(ConditionalCleanFixtureController)]);

        act.Should().NotThrow("409 and the success code are the action's own to declare");
    }

    [Fact]
    public void NonConditionalActionDeclaring412_Passes()
    {
        var act = () => ArchitectureRules.ConditionalActionsDoNotRedeclarePreconditionResponses(
            [typeof(NonConditional412FixtureController)]);

        act.Should().NotThrow("without [SupportsIfMatch] nothing contributes the 412, so declaring it is correct");
    }

    // -- Rule 8: the fact runs over module API layers only --
    [Fact]
    public void Fact_ScansModuleApiLayers()
    {
        var assert = new ModuleLayerTests().ConditionalActions_ShouldNotRedeclare_PreconditionResponses;

        var message = assert.Should().Throw<Exception>().Which.Message;

        message.Should().Contain(nameof(Conditional412FixtureController));
        message.Should().Contain(nameof(Conditional428FixtureController));
        message.Should().NotContain(nameof(ConditionalCleanFixtureController));
        message.Should().NotContain(nameof(NonConditional412FixtureController));
    }

    [Fact]
    public void Fact_IgnoresFrameworkApiLayers()
    {
        var frameworkFixtures = new FrameworkLayerTests().ConditionalActions_ShouldNotRedeclare_PreconditionResponses;
        var commonItself = new CommonMapTests().ConditionalActions_ShouldNotRedeclare_PreconditionResponses;

        frameworkFixtures.Should().NotThrow("a framework API layer (Module empty) is outside the rule");
        commonItself.Should().NotThrow(
            "CrudEntityControllerBase.UpdateAsync keeps its explicit 412/428 stack and its document order");
    }

    /// <summary>Declares this assembly, where the fixtures compile, as a module's API layer.</summary>
    private sealed class ModuleApiMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Module("Fixtures", Layer.Api, typeof(Conditional412FixtureController).Assembly)];
    }

    /// <summary>Declares the same assembly as a framework API layer.</summary>
    private sealed class FrameworkApiMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Framework(Layer.Api, typeof(Conditional412FixtureController).Assembly)];
    }

    private sealed class ModuleLayerTests : ControllerConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = new ModuleApiMap();
    }

    private sealed class FrameworkLayerTests : ControllerConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = new FrameworkApiMap();
    }

    private sealed class CommonMapTests : ControllerConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
    }
}
