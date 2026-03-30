namespace MMCA.Common.Shared.Notifications.PushNotifications;

/// <summary>
/// Request record for sending a push notification to all recipients.
/// </summary>
public sealed record SendPushNotificationRequest(string Title, string Body);
