using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Layering;

/// <summary>
/// Verifies the <c>NamespacesHaveNoDependencyCycles</c> fitness function against deliberately-shaped
/// fixture namespaces under <c>CycleFixtures</c>: it flags the two-namespace cycle (<c>Left</c> holds a
/// <c>Right</c> property, <c>Right</c> derives from a <c>Left</c> base) and leaves the acyclic namespace
/// alone.
/// </summary>
public sealed class NamespaceCycleFitnessTests
{
    private const string FixtureRoot = "MMCA.Common.Architecture.Tests.CycleFixtures";

    [Fact]
    public void Rule_FlagsTwoNamespaceCycle_ButNotAcyclicNamespaces()
    {
        var act = () => ArchitectureRules.NamespacesHaveNoDependencyCycles(new CycleTestMap());

        var exception = act.Should().Throw<Exception>().Which;
        exception.Message.Should().Contain($"{FixtureRoot}.Left", "Left references Right through a property");
        exception.Message.Should().Contain($"{FixtureRoot}.Right", "Right derives from a Left base type");
        exception.Message.Should().NotContain(
            $"{FixtureRoot}.Acyclic",
            "the acyclic fixture namespace only points one way and nothing points back at it");
    }

    [Fact]
    public void Rule_Passes_WhenTheWholeCycleIsAllowed()
    {
        var act = () => ArchitectureRules.NamespacesHaveNoDependencyCycles(
            new CycleTestMap(),
            [$"{FixtureRoot}.Left", $"{FixtureRoot}.Right"]);

        act.Should().NotThrow("an allowance covering every namespace of the component accepts the cycle");
    }

    [Fact]
    public void Rule_StillFails_WhenOnlyPartOfTheCycleIsAllowed()
    {
        var act = () => ArchitectureRules.NamespacesHaveNoDependencyCycles(
            new CycleTestMap(),
            [$"{FixtureRoot}.Left"]);

        act.Should().Throw<Exception>("a partial allowance must never hide a cycle");
    }

    /// <summary>
    /// A map whose single layer is this test assembly rooted at the fixture namespace, so the rule sees
    /// only the <c>CycleFixtures.*</c> types and none of the real test code.
    /// </summary>
    private sealed class CycleTestMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            new LayerRef(
                string.Empty,
                Layer.Application,
                typeof(NamespaceCycleFitnessTests).Assembly,
                FixtureRoot),
        ];
    }
}
