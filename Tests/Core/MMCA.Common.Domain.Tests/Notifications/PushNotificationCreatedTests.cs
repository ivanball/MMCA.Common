using AwesomeAssertions;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Notifications.PushNotifications.DomainEvents;

namespace MMCA.Common.Domain.Tests.Notifications;

public sealed class PushNotificationCreatedTests
{
    // ── Properties accessible ──
    [Fact]
    public void Create_StoresAllProperties()
    {
        var evt = new PushNotificationCreated(42, "Test Title", 10);

        evt.NotificationId.Should().Be(42);
        evt.Title.Should().Be("Test Title");
        evt.RecipientCount.Should().Be(10);
    }

    // ── DateOccurred set automatically ──
    [Fact]
    public void Create_HasDateOccurredSetToApproximatelyUtcNow()
    {
        var before = DateTime.UtcNow;
        var evt = new PushNotificationCreated(1, "Title", 5);
        var after = DateTime.UtcNow;

        evt.DateOccurred.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // ── Inherits from BaseDomainEvent ──
    [Fact]
    public void Create_InheritsFromBaseDomainEvent() =>
        typeof(PushNotificationCreated).Should().BeDerivedFrom<BaseDomainEvent>();

    // ── Implements IDomainEvent ──
    [Fact]
    public void Create_ImplementsIDomainEvent()
    {
        var evt = new PushNotificationCreated(1, "Title", 1);

        evt.Should().BeAssignableTo<IDomainEvent>();
    }

    // ── Record equality ──
    [Fact]
    public void TwoInstancesWithSameValues_AreEqual()
    {
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var evt1 = new PushNotificationCreated(1, "Title", 5) { DateOccurred = date };
        var evt2 = new PushNotificationCreated(1, "Title", 5) { DateOccurred = date };

        evt1.Should().Be(evt2);
    }

    // ── Default notification ID ──
    [Fact]
    public void Create_WithDefaultNotificationId_HasZeroId()
    {
        var evt = new PushNotificationCreated(default, "Title", 3);

        evt.NotificationId.Should().Be(0);
    }

    // ── Zero recipient count ──
    [Fact]
    public void Create_WithZeroRecipients_StoresZero()
    {
        var evt = new PushNotificationCreated(1, "Title", 0);

        evt.RecipientCount.Should().Be(0);
    }
}
