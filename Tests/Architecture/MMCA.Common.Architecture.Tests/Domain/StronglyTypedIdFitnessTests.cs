using MMCA.Common.Architecture.Tests.StronglyTypedIdFixtures;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Domain;

/// <summary>
/// Verifies the <c>StronglyTypedIdsAreReadonlyRecordStructs</c> fitness function (ADR-115): it flags
/// a hand-rolled struct, a mutable record struct and a wrapper carrying extra state, and says
/// nothing about the two compliant shapes.
/// </summary>
public sealed class StronglyTypedIdFitnessTests
{
    [Fact]
    public void Rule_FlagsAWrapperThatIsNotARecord()
    {
        var act = () => ArchitectureRules.StronglyTypedIdsAreReadonlyRecordStructs(new IdentifierFixtureMap());

        act.Should().Throw<Exception>().Which.Message
            .Should().Contain(nameof(NotARecordFixtureId))
            .And.Contain("record");
    }

    [Fact]
    public void Rule_FlagsAWrapperThatIsNotReadonly()
    {
        var act = () => ArchitectureRules.StronglyTypedIdsAreReadonlyRecordStructs(new IdentifierFixtureMap());

        act.Should().Throw<Exception>().Which.Message
            .Should().Contain(nameof(MutableFixtureId))
            .And.Contain("readonly");
    }

    [Fact]
    public void Rule_FlagsAWrapperCarryingExtraState()
    {
        var act = () => ArchitectureRules.StronglyTypedIdsAreReadonlyRecordStructs(new IdentifierFixtureMap());

        act.Should().Throw<Exception>().Which.Message
            .Should().Contain(nameof(ExtraStateFixtureId))
            .And.Contain("Label");
    }

    [Fact]
    public void Rule_SaysNothingAboutTheCanonicalDeclaration()
    {
        var act = () => ArchitectureRules.StronglyTypedIdsAreReadonlyRecordStructs(new IdentifierFixtureMap());

        var message = act.Should().Throw<Exception>().Which.Message;
        message.Should().NotContain(nameof(CompliantFixtureId));
        message.Should().NotContain(nameof(CompliantStringFixtureId));
    }

    private sealed class IdentifierFixtureMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Framework(Layer.Shared, typeof(StronglyTypedIdFitnessTests).Assembly)];
    }
}
