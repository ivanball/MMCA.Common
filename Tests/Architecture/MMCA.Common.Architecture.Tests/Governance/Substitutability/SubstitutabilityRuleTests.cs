using System.Collections.Frozen;
using MMCA.Common.Architecture.Tests.SubstitutabilityFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Governance.Substitutability;

/// <summary>
/// Self-test for the substitutability rule shipped in <c>MMCA.Common.Testing.Architecture</c>
/// (<see cref="SubstitutabilityTestsBase"/>). A rule that reads IL cannot be verified by reading it,
/// so this points a map at THIS assembly, which compiles the offending and the innocent members side
/// by side in <c>SubstitutabilityFixtures</c>, and pins every behaviour: a refusing override and an
/// unwritten interface implementation are caught, a plain throwing method is not, and an excluded
/// member is silenced by name.
/// </summary>
public sealed class SubstitutabilityRuleTests
{
    private static readonly string ExcludedMember = $"{typeof(FixtureExcludedShape).FullName}.{nameof(FixtureExcludedShape.Corners)}";

    private readonly FixtureAssemblyMap _map = new();

    [Fact]
    public void OverrideThrowingNotSupported_IsFlagged()
    {
        var act = () => ArchitectureRules.SubstitutableMembersDoNotRefuseTheContract(_map, FrozenSet<string>.Empty);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain($"{nameof(FixtureRefusingShape)}.{nameof(FixtureRefusingShape.Describe)}");
        message.Should().Contain("System.NotSupportedException", "the report must name the exception thrown");
    }

    [Fact]
    public void InterfaceImplementationThrowingNotImplemented_IsFlagged()
    {
        var act = () => ArchitectureRules.SubstitutableMembersDoNotRefuseTheContract(_map, FrozenSet<string>.Empty);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain($"{nameof(FixtureUnfinishedExporter)}.{nameof(FixtureUnfinishedExporter.Export)}");
        message.Should().Contain("System.NotImplementedException");
    }

    [Fact]
    public void NonOverrideMethodThatThrows_IsNotFlagged()
    {
        var act = () => ArchitectureRules.SubstitutableMembersDoNotRefuseTheContract(_map, FrozenSet<string>.Empty);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(FixtureStandaloneThrower),
                "a method that overrides nothing and implements nothing promised the operation to no caller");
    }

    [Fact]
    public void ExcludedMember_IsNotFlagged()
    {
        var unexcluded = () => ArchitectureRules.SubstitutableMembersDoNotRefuseTheContract(_map, FrozenSet<string>.Empty);
        unexcluded.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(ExcludedMember, "without the exclusion the refusing override is reported");

        var excluded = () => ArchitectureRules.SubstitutableMembersDoNotRefuseTheContract(
            _map,
            FrozenSet.Create(StringComparer.Ordinal, ExcludedMember));

        var message = excluded.Should().Throw<XunitException>("the other fixtures still refuse their contracts").Which.Message;

        message.Should().NotContain(ExcludedMember, "an excluded member is the reviewed, deliberate refusal");
        message.Should().Contain(nameof(FixtureRefusingShape), "the exclusion silences only the member it names");
    }

    /// <summary>A module-less map over this test assembly, so the rule scans the fixtures above.</summary>
    private sealed class FixtureAssemblyMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Framework(Layer.Domain, typeof(FixtureRefusingShape).Assembly),
        ];
    }
}
