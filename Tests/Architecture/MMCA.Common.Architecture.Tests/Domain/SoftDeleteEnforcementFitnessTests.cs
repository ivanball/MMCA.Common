using MMCA.Common.Architecture.Tests.HardDeleteFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Domain;

/// <summary>
/// Self-test for the hard-delete fitness rule shipped in <c>MMCA.Common.Testing.Architecture</c>
/// (<see cref="SoftDeleteEnforcementTestsBase"/>). A rule that reads IL cannot be verified by
/// reading it, so this points a map at THIS assembly, which compiles the offending and the innocent
/// call sites side by side in <c>HardDeleteFixtures</c>, and pins all three behaviours: EF's erasing
/// members are caught, an allowlist entry silences exactly the type it names, and an ordinary
/// collection <c>Remove</c> is never mistaken for one.
/// </summary>
public sealed class SoftDeleteEnforcementFitnessTests
{
    private const string FixtureNamespace = "MMCA.Common.Architecture.Tests.HardDeleteFixtures";

    private readonly FixtureAssemblyMap _map = new();

    [Fact]
    public void HardDelete_OutsideTheAllowlist_IsFlagged()
    {
        var act = () => ArchitectureRules.HardDeletesOnlyInAllowedTypes(_map, []);

        act.Should().Throw<XunitException>()
            .WithMessage("*DbSetRemovingFixture*", "DbSet.Remove erases a row and must be reported");
    }

    [Fact]
    public void HardDelete_ReportsEveryErasingMember()
    {
        var act = () => ArchitectureRules.HardDeletesOnlyInAllowedTypes(_map, []);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain("Purge", "DbSet.Remove is an erasing member");
        message.Should().Contain("PurgeMany", "DbSet.RemoveRange is an erasing member");
        message.Should().Contain("PurgeAsync", "ExecuteDeleteAsync is an erasing member");
    }

    [Fact]
    public void OrdinaryCollectionRemove_IsNotAHardDelete()
    {
        var act = () => ArchitectureRules.HardDeletesOnlyInAllowedTypes(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(SoftDeletingFixture),
                "List<T>.Remove and the framework's own Delete() are not hard deletes");
    }

    [Fact]
    public void AllowlistedNamespace_SilencesTheRule()
    {
        var act = () => ArchitectureRules.HardDeletesOnlyInAllowedTypes(_map, [FixtureNamespace]);

        act.Should().NotThrow(
            "a namespace entry covers the purge types under it, which is how a repo records its reviewed erasure sites");
    }

    [Fact]
    public void AllowlistedType_SilencesOnlyThatType()
    {
        var allowed = $"{FixtureNamespace}.{nameof(DbSetRemovingFixture)}";

        var act = () => ArchitectureRules.HardDeletesOnlyInAllowedTypes(_map, [allowed]);

        var message = act.Should().Throw<XunitException>(
            "the ExecuteDelete fixture is still outside the allowlist").Which.Message;

        message.Should().NotContain(nameof(DbSetRemovingFixture));
        message.Should().Contain(nameof(ExecuteDeletingFixture));
    }

    /// <summary>A map whose single layer is this test assembly, so the rule scans the fixtures above.</summary>
    private sealed class FixtureAssemblyMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Framework(Layer.Infrastructure, typeof(FixtureEntity).Assembly),
        ];
    }
}
