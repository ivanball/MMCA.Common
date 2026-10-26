using MMCA.Common.API.Controllers;
using MMCA.Common.Architecture.Tests.AiBoundaryFixtures;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Layering.GovernedAi;

/// <summary>
/// M257: pins that the type-level governed-AI rule actually matches <c>MMCA.Common.AI</c> types. The
/// rule once matched the dotted entry <c>MMCA.Common.AI.</c>, which NetArchTest never matches (it
/// compares whole namespace segments), so the gate passed vacuously in every subclass. It also pins
/// that the undotted root does not catch <c>MMCA.Common.API</c>, a different segment.
/// </summary>
public sealed class AiBoundaryRulesTests
{
    [Fact]
    public void ALayerNamingAGovernedAiType_FailsTheRule()
    {
        var act = () => ArchitectureRules.GovernedAiPackageStaysBehindInfrastructure(
            new SingleLayerMap(typeof(FixtureAiConsumer).Assembly));

        var message = act.Should().Throw<Exception>().Which.Message;
        message.Should().Contain("must not reference MMCA.Common.AI");
        message.Should().Contain(nameof(FixtureAiConsumer));
    }

    [Fact]
    public void TheApiAssembly_IsNotMistakenForTheAiPackage()
    {
        var act = () => ArchitectureRules.GovernedAiPackageStaysBehindInfrastructure(
            new SingleLayerMap(typeof(ApiControllerBase).Assembly));

        act.Should().NotThrow("API and AI are different namespace segments");
    }

    /// <summary>A map whose single Application layer is the given assembly.</summary>
    private sealed class SingleLayerMap(Assembly assembly) : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Framework(Layer.Application, assembly)];
    }
}
