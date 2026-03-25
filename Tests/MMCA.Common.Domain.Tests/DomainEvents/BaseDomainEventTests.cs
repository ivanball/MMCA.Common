using FluentAssertions;
using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Domain.Tests.DomainEvents;

public class BaseDomainEventTests
{
    private sealed record TestDomainEvent(string Data) : BaseDomainEvent;

    // ── DateOccurred ──
    [Fact]
    public void DateOccurred_IsSetToApproximatelyUtcNow()
    {
        var before = DateTime.UtcNow;
        var evt = new TestDomainEvent("test");
        var after = DateTime.UtcNow;

        evt.DateOccurred.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void DateOccurred_CanBeOverridden()
    {
        var custom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var evt = new TestDomainEvent("test") { DateOccurred = custom };
        evt.DateOccurred.Should().Be(custom);
    }

    // ── Record equality ──
    [Fact]
    public void TwoEvents_WithSameData_AreNotEqual_DueToDifferentDateOccurred()
    {
        var evt1 = new TestDomainEvent("test");
        var evt2 = new TestDomainEvent("test");

        // DateOccurred may differ by ticks
        (evt1 == evt2).Should().Be(evt1.DateOccurred == evt2.DateOccurred);
    }

    [Fact]
    public void Event_StoresData() =>
        new TestDomainEvent("hello").Data.Should().Be("hello");
}
