using AwesomeAssertions;
using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Domain.Tests.DomainEvents;

public sealed class BaseIntegrationEventTests
{
    // ── Creates with DateOccurred ──
    [Fact]
    public void Create_HasDateOccurredSetToApproximatelyUtcNow()
    {
        var before = DateTime.UtcNow;
        var evt = new TestIntegrationEvent("test-data");
        var after = DateTime.UtcNow;

        evt.DateOccurred.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // ── Properties accessible ──
    [Fact]
    public void Create_StoresPayloadData()
    {
        var evt = new TestIntegrationEvent("my-payload");

        evt.Payload.Should().Be("my-payload");
    }

    // ── DateOccurred can be overridden ──
    [Fact]
    public void Create_DateOccurred_CanBeOverridden()
    {
        var customDate = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var evt = new TestIntegrationEvent("data") { DateOccurred = customDate };

        evt.DateOccurred.Should().Be(customDate);
    }

    // ── Implements IIntegrationEvent ──
    [Fact]
    public void Create_ImplementsIIntegrationEvent()
    {
        var evt = new TestIntegrationEvent("data");

        evt.Should().BeAssignableTo<MMCA.Common.Domain.Interfaces.IIntegrationEvent>();
    }
}

// ── Test helpers ──
public sealed record TestIntegrationEvent(string Payload) : BaseIntegrationEvent;
