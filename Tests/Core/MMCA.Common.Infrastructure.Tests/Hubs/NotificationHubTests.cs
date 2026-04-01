using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using MMCA.Common.Infrastructure.Hubs;

namespace MMCA.Common.Infrastructure.Tests.Hubs;

public sealed class NotificationHubTests
{
    [Fact]
    public void ReceiveNotificationMethod_HasExpectedValue() =>
        NotificationHub.ReceiveNotificationMethod.Should().Be("ReceiveNotification");

    [Fact]
    public void NotificationHub_HasAuthorizeAttribute()
    {
        var attribute = typeof(NotificationHub)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false);

        attribute.Should().ContainSingle();
    }

    [Fact]
    public void NotificationHub_InheritsFromHub() =>
        typeof(NotificationHub).Should().BeDerivedFrom<Microsoft.AspNetCore.SignalR.Hub>();

    [Fact]
    public void NotificationHub_IsSealed() =>
        typeof(NotificationHub).IsSealed.Should().BeTrue();
}
