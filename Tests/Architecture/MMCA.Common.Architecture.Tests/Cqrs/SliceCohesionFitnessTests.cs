using MMCA.Common.Architecture.Tests.SliceFixtures;
using MMCA.Common.Architecture.Tests.SliceFixtures.Stranded;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Cqrs;

/// <summary>
/// Self-test for the slice-cohesion rule shipped in <c>MMCA.Common.Testing.Architecture</c>
/// (<see cref="SliceCohesionTestsBase"/>), pinning the behaviour the gate gained when it stopped
/// reading concrete classes only: an ABSTRACT handler base is now scanned. It points a map at THIS
/// assembly, whose <c>SliceFixtures</c> compile the three shapes that matter - an abstract base
/// stranded away from its concrete contract, an abstract base declared beside it, and an abstract
/// base parameterized over its contract (the framework's own <c>*HandlerBase</c> shape, which has a
/// generic-parameter contract nobody can co-locate).
/// </summary>
public sealed class SliceCohesionFitnessTests
{
    private readonly FixtureApplicationMap _map = new();

    [Fact]
    public void AnAbstractHandlerStrandedFromItsContract_IsFlagged()
    {
        var act = () => ArchitectureRules.HandlersAreCoLocatedWithTheirContracts(_map);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                nameof(StrandedFixtureHandlerBase),
                "an abstract base is the shape consumers derive from, so a base stranded away from its contract strands every handler built on it");
    }

    [Fact]
    public void AnAbstractHandlerBesideItsContract_IsNotFlagged()
    {
        var act = () => ArchitectureRules.HandlersAreCoLocatedWithTheirContracts(_map);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(GetFixturePreferencesHandlerBase),
                "the base and the query it serves share one namespace, which is exactly what the rule asks for");
    }

    [Fact]
    public void AnAbstractHandlerOverAGenericContract_IsSkipped()
    {
        var act = () => ArchitectureRules.HandlersAreCoLocatedWithTheirContracts(_map);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(GetFixtureEntityHandlerBase<>),
                "a contract that is a type parameter is not a type any folder can hold, so the framework's own generic handler bases stay exempt");
    }

    /// <summary>
    /// A map registering this test assembly as the Application layer, which is where the rule reads
    /// its handlers from.
    /// </summary>
    private sealed class FixtureApplicationMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Framework(Layer.Application, typeof(GetFixturePreferencesQuery).Assembly),
        ];
    }
}
