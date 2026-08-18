using AwesomeAssertions;
using MMCA.Common.Domain.IntegrationEvents;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.Tests.IntegrationEvents;

/// <summary>
/// Shape tests for <see cref="OutputCacheEvictionRequested"/>. It is a frozen-contract candidate:
/// every host that consumes it must be able to deserialize it forever, so the members, the schema
/// version and the empty-tags default are asserted rather than assumed.
/// </summary>
public sealed class OutputCacheEvictionRequestedTests
{
    [Fact]
    public void IsAnIntegrationEvent_SoItLeavesTheProcessThroughTheOutbox() =>
        new OutputCacheEvictionRequested().Should().BeAssignableTo<IIntegrationEvent>();

    [Fact]
    public void CarriesTheTagsItWasGiven() =>
        new OutputCacheEvictionRequested { Tags = ["conference:sessions", "conference:speakers"] }
            .Tags.Should().Equal("conference:sessions", "conference:speakers");

    // A message that arrives without the field must deserialize into a harmless no-op rather than
    // faulting the consumer and dead-lettering.
    [Fact]
    public void Tags_DefaultToEmpty() =>
        new OutputCacheEvictionRequested().Tags.Should().BeEmpty();

    [Fact]
    public void CarriesTheOutboxIdentityEveryIntegrationEventNeeds()
    {
        var @event = new OutputCacheEvictionRequested();

        @event.MessageId.Should().NotBeEmpty(because: "the inbox dedups redeliveries on it");
        @event.DateOccurred.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // Bumping this is a wire-contract change (ADR-010), not a refactor: it must be a deliberate,
    // reviewed edit paired with a consumer-side upcaster, so the current value is pinned here.
    [Fact]
    public void SchemaVersion_IsOne() =>
        new OutputCacheEvictionRequested().SchemaVersion.Should().Be(1);
}
