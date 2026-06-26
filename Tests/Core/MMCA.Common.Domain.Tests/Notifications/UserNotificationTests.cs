using AwesomeAssertions;
using MMCA.Common.Domain.Notifications.UserNotifications;

namespace MMCA.Common.Domain.Tests.Notifications;

public class UserNotificationTests
{
    [Fact]
    public void Create_ReturnsUnreadNotification()
    {
        var notification = UserNotification.Create(userId: 1, pushNotificationId: 42).Value!;

        notification.UserId.Should().Be(1);
        notification.PushNotificationId.Should().Be(42);
        notification.IsRead.Should().BeFalse();
        notification.ReadOn.Should().BeNull();
    }

    [Fact]
    public void MarkAsRead_SetsIsReadAndReadOn()
    {
        var notification = UserNotification.Create(userId: 1, pushNotificationId: 42).Value!;
        var readOn = new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Utc);

        notification.MarkAsRead(readOn);

        notification.IsRead.Should().BeTrue();
        notification.ReadOn.Should().Be(readOn);
    }

    [Fact]
    public void MarkAsRead_IsIdempotent()
    {
        var notification = UserNotification.Create(userId: 1, pushNotificationId: 42).Value!;
        var firstReadOn = new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Utc);
        var laterReadOn = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

        notification.MarkAsRead(firstReadOn);
        notification.MarkAsRead(laterReadOn);

        notification.ReadOn.Should().Be(firstReadOn);
    }
}
