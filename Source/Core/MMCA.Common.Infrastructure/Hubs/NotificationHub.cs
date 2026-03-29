using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MMCA.Common.Infrastructure.Hubs;

/// <summary>
/// SignalR hub for push notifications. The hub itself is thin — it maps authenticated user
/// connections. Notification delivery is handled by <see cref="Services.SignalRPushNotificationSender"/>
/// using <see cref="IHubContext{THub}"/>.
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    /// <summary>The SignalR method name clients listen on to receive notifications.</summary>
    public const string ReceiveNotificationMethod = "ReceiveNotification";
}
