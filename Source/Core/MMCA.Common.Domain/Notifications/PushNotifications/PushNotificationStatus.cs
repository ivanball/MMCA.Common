namespace MMCA.Common.Domain.Notifications.PushNotifications;

/// <summary>
/// Represents the delivery status of a push notification.
/// </summary>
public enum PushNotificationStatus
{
    /// <summary>Notification has been created but not yet sent.</summary>
    Pending,

    /// <summary>Notification was successfully sent to all recipients.</summary>
    Sent,

    /// <summary>Notification delivery failed.</summary>
    Failed
}
