namespace MMCA.Common.Shared.Notifications.UserNotifications;

/// <summary>
/// Data transfer object representing a notification in a user's inbox.
/// Combines <c>UserNotification</c> read-tracking with <c>PushNotification</c> content.
/// </summary>
public sealed record class UserNotificationDTO
{
    /// <summary>Gets or inits the user notification identifier.</summary>
    public required UserNotificationIdentifierType Id { get; init; }

    /// <summary>Gets or inits the push notification identifier.</summary>
    public required PushNotificationIdentifierType PushNotificationId { get; init; }

    /// <summary>Gets or inits the notification title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets or inits the notification body.</summary>
    public required string Body { get; init; }

    /// <summary>Gets or inits whether the notification has been read.</summary>
    public required bool IsRead { get; init; }

    /// <summary>Gets or inits the timestamp when the notification was read.</summary>
    public DateTime? ReadOn { get; init; }

    /// <summary>Gets or inits the timestamp when the notification was sent.</summary>
    public DateTime SentOn { get; init; }
}
