using MMCA.Common.Architecture.Tests.CascadeFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Self-test for the cascade-soft-delete fitness rule shipped in
/// <c>MMCA.Common.Testing.Architecture</c> (<see cref="CascadeSoftDeleteConventionTestsBase"/>). A
/// rule that reads IL cannot be verified by reading it, so this points a map at THIS assembly, which
/// compiles the cascading and the non-cascading aggregate shapes side by side in
/// <c>CascadeFixtures</c>, and pins all five behaviours: both cascade forms pass, a missing override
/// and an override that only deletes itself are reported with their reason and their child
/// collection, an aggregate with no child entities is ignored, and an exemption silences exactly the
/// type it names.
/// </summary>
public sealed class CascadeSoftDeleteFitnessTests
{
    private const string FixtureNamespace = "MMCA.Common.Architecture.Tests.CascadeFixtures";

    private readonly FixtureAssemblyMap _map = new();

    [Fact]
    public void AggregateWithoutDeleteOverride_IsFlagged_WithItsChildCollection()
    {
        var act = () => ArchitectureRules.AggregatesCascadeSoftDeleteToChildren(_map, []);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain(nameof(MissingOverrideFixture), "it owns children and never overrides Delete()");
        message.Should().Contain("no Delete() override", "the report must say WHY the aggregate is offending");
        message.Should().Contain("_orphans", "the report must name the child collection that stays active");
    }

    [Fact]
    public void DeleteOverrideThatOnlyDeletesItself_IsFlagged_WithItsChildCollection()
    {
        var act = () => ArchitectureRules.AggregatesCascadeSoftDeleteToChildren(_map, []);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain(nameof(SelfOnlyDeleteFixture), "base.Delete() deletes the root, not its children");
        message.Should().Contain("Delete() never deletes a child", "the report must distinguish this from a missing override");
        message.Should().Contain("_ignored", "the report must name the child collection that stays active");
    }

    [Fact]
    public void BothCascadeForms_AreAccepted()
    {
        var act = () => ArchitectureRules.AggregatesCascadeSoftDeleteToChildren(_map, []);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().NotContain(
            nameof(HelperCascadingFixture),
            "DeleteChildren<TChild, TChildId>(...) is the framework's cascade helper");
        message.Should().NotContain(
            nameof(LoopCascadingFixture),
            "a hand-rolled foreach calling each child's Delete() cascades just as well");
    }

    [Fact]
    public void AggregateWithNoChildEntities_IsIgnored()
    {
        var act = () => ArchitectureRules.AggregatesCascadeSoftDeleteToChildren(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(ChildlessFixture),
                "a List<string> is not a child-entity collection, so there is nothing to cascade to");
    }

    [Fact]
    public void AllowlistedType_SilencesOnlyThatType()
    {
        var allowed = $"{FixtureNamespace}.{nameof(ExemptedOffenderFixture)}";

        var act = () => ArchitectureRules.AggregatesCascadeSoftDeleteToChildren(_map, [allowed]);

        var message = act.Should().Throw<XunitException>(
            "the other offenders are still outside the exemption list").Which.Message;

        message.Should().NotContain(nameof(ExemptedOffenderFixture));
        message.Should().Contain(nameof(MissingOverrideFixture));
    }

    [Fact]
    public void AllowlistedNamespace_SilencesTheRule()
    {
        var act = () => ArchitectureRules.AggregatesCascadeSoftDeleteToChildren(_map, [FixtureNamespace]);

        act.Should().NotThrow(
            "a namespace entry covers the aggregates under it, which is how a repo records the roots whose children deliberately outlive them");
    }

    /// <summary>A map whose single layer is this test assembly, so the rule scans the fixtures above.</summary>
    private sealed class FixtureAssemblyMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Framework(Layer.Domain, typeof(CascadeChildFixture).Assembly),
        ];
    }
}
