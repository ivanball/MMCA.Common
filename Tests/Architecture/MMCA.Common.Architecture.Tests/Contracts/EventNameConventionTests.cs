using MMCA.Common.Architecture.Tests.EventNameFixtures.IntegrationEvents;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Contracts;

/// <summary>
/// Adversarial coverage for <see cref="ArchitectureRules.IntegrationEventsDeclareEventName"/>, which
/// <see cref="EventConventionTestsBase"/> runs for every consumer. The rule is pointed at THIS test
/// assembly, whose fixtures include an event without <c>[EventName]</c> beside one that declares it:
/// the first must be named and the second must not. The framework's own events are covered by
/// <see cref="EventVersioningConventionTests"/>, which inherits the same fact.
/// </summary>
public sealed class EventNameConventionTests
{
    [Fact]
    public void EventWithoutEventName_IsFlagged()
    {
        var act = () => ArchitectureRules.IntegrationEventsDeclareEventName(new FixtureProbeMap());

        var message = act.Should().Throw<Exception>().Which.Message;

        message.Should().Contain(nameof(FixtureEventNameMissingEvent), "an event with no [EventName] persists a CLR name a rename orphans");
        message.Should().Contain("[EventName", "the report must say what to add");
    }

    [Fact]
    public void EventWithEventName_IsNotFlagged()
    {
        var act = () => ArchitectureRules.IntegrationEventsDeclareEventName(new FixtureProbeMap());

        act.Should().Throw<Exception>().Which.Message
            .Should().NotContain(
                nameof(FixtureEventNameDeclaredEvent),
                "an event declaring its stable wire identity is exactly what the rule asks for");
    }

    [Fact]
    public void FrameworkEvents_AllDeclareEventName() =>
        FluentActions.Invoking(() => ArchitectureRules.IntegrationEventsDeclareEventName(new CommonArchitectureMap()))
            .Should().NotThrow("every integration event MMCA.Common ships declares its [EventName]");

    /// <summary>A module-less map over this test assembly, so every fixture event is scanned.</summary>
    private sealed class FixtureProbeMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Framework(Layer.Shared, typeof(FixtureEventNameMissingEvent).Assembly)];
    }
}
