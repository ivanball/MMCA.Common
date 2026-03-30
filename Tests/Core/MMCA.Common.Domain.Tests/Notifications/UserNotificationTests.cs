using AwesomeAssertions;
using MMCA.Common.Domain.Notifications.UserNotifications;

namespace MMCA.Common.Domain.Tests.Notifications;

public class UserNotificationTests
{
    [Fact]
    public void Create_ReturnsUnreadNotification()
    {
        var notification = UserNotification.Create(userId: 1, pushNotificationId: 42);

        notification.UserId.Should().Be(1);
        notification.PushNotificationId.Should().Be(42);
        notification.IsRead.Should().BeFalse();
        notification.ReadOn.Should().BeNull();
    }

    [Fact]
    public void MarkAsRead_SetsIsReadAndReadOn()
    {
        var notification = UserNotification.Create(userId: 1, pushNotificationId: 42);

        notification.MarkAsRead();

        notification.IsRead.Should().BeTrue();
        notification.ReadOn.Should().NotBeNull();
    }

    [Fact]
    public void MarkAsRead_IsIdempotent()
    {
        var notification = UserNotification.Create(userId: 1, pushNotificationId: 42);

        notification.MarkAsRead();
        DateTime? firstReadOn = notification.ReadOn;

        notification.MarkAsRead();

        notification.ReadOn.Should().Be(firstReadOn);
    }
}
