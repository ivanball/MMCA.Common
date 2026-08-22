using MMCA.Common.Architecture.Tests.UpcasterFixtures.IntegrationEvents;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Verifies the two event-upcaster fitness functions (ADR-090) against deliberately-shaped fixtures
/// under <c>UpcasterFixtures</c>: the unique-source rule flags a contract claimed by two upcasters,
/// the version rule flags an upcaster pointing at a LOWER <c>SchemaVersion</c>, and neither touches
/// the compliant ladder. A map that owns no upcasters (the framework's own, today) passes both.
/// </summary>
public sealed class EventUpcasterFitnessTests
{
    [Fact]
    public void UniqueSourceRule_FlagsTheContestedContract_ButNotTheCompliantLadder()
    {
        var message = RunUniqueSourceRule();

        message.Should().Contain(nameof(FixtureContestedV1), "two upcasters read that contract");
        message.Should().Contain(nameof(FixtureContestedClaimUpcaster));
        message.Should().Contain(nameof(FixtureRivalClaimUpcaster));
        message.Should().NotContain(
            nameof(FixtureCompliantV1ToV2Upcaster),
            "each rung of the compliant ladder is the only claimant on its source");
    }

    /// <summary>
    /// The compliant ladder chains V1 to V2 to V3, so its middle contract is both a target and a
    /// source. That is a chain, not a duplicate claim, and the rule must leave it alone.
    /// </summary>
    [Fact]
    public void UniqueSourceRule_DoesNotFlag_AnUpcasterWhoseSourceIsAnotherUpcastersTarget() =>
        RunUniqueSourceRule().Should().NotContain(nameof(FixtureCompliantV2ToV3Upcaster));

    [Fact]
    public void SchemaVersionRule_FlagsTheBackwardsUpcaster_ButNotTheCompliantLadder()
    {
        var message = RunSchemaVersionRule();

        message.Should().Contain(nameof(FixtureBackwardsVersionUpcaster));
        message.Should().Contain(
            "must declare a HIGHER SchemaVersion",
            "the message has to say which direction an upcaster is allowed to move");
        message.Should().NotContain(nameof(FixtureCompliantV1ToV2Upcaster));
        message.Should().NotContain(nameof(FixtureCompliantV2ToV3Upcaster));
    }

    [Fact]
    public void BothRules_Pass_OnAMapThatOwnsNoUpcasters()
    {
        var map = new CommonArchitectureMap();

        FluentActions.Invoking(() => ArchitectureRules.EventUpcastersHaveUniqueSourceTypes(map))
            .Should().NotThrow("the framework ships no upcaster of its own, so the rule passes vacuously");
        FluentActions.Invoking(() => ArchitectureRules.EventUpcastersIncreaseSchemaVersion(map))
            .Should().NotThrow();
    }

    private static string RunUniqueSourceRule()
    {
        var act = () => ArchitectureRules.EventUpcastersHaveUniqueSourceTypes(new UpcasterTestMap());

        return act.Should().Throw<Exception>().Which.Message;
    }

    private static string RunSchemaVersionRule()
    {
        var act = () => ArchitectureRules.EventUpcastersIncreaseSchemaVersion(new UpcasterTestMap());

        return act.Should().Throw<Exception>().Which.Message;
    }

    /// <summary>A map whose single Application layer is this test assembly, so the fixtures are in scope.</summary>
    private sealed class UpcasterTestMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Framework(Layer.Application, typeof(EventUpcasterFitnessTests).Assembly)];
    }
}
