using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Domain.Notifications.UserNotifications;

/// <summary>
/// Tracks delivery of a <see cref="PushNotifications.PushNotification"/> to an individual user.
/// Enables per-user inbox with read/unread state.
/// </summary>
[IdValueGenerated]
public sealed class UserNotification : AuditableAggregateRootEntity<UserNotificationIdentifierType>
{
    /// <summary>Gets the recipient user identifier.</summary>
    public UserIdentifierType UserId { get; private set; }

    /// <summary>Gets the associated push notification identifier.</summary>
    public PushNotificationIdentifierType PushNotificationId { get; private set; }

    /// <summary>Gets a value indicating whether the notification has been read.</summary>
    public bool IsRead { get; private set; }

    /// <summary>Gets the timestamp when the notification was read.</summary>
    public DateTime? ReadOn { get; private set; }

    /// <summary>EF Core parameterless constructor.</summary>
    private UserNotification()
    {
    }

    private UserNotification(UserIdentifierType userId, PushNotificationIdentifierType pushNotificationId)
    {
        UserId = userId;
        PushNotificationId = pushNotificationId;
        IsRead = false;
    }

    /// <summary>
    /// Factory method that creates a new <see cref="UserNotification"/> for a recipient.
    /// </summary>
    /// <param name="userId">The recipient user identifier.</param>
    /// <param name="pushNotificationId">The push notification identifier.</param>
    /// <returns>A new <see cref="UserNotification"/> instance.</returns>
    public static UserNotification Create(
        UserIdentifierType userId,
        PushNotificationIdentifierType pushNotificationId) =>
        new(userId, pushNotificationId) { Id = default };

    /// <summary>
    /// Marks the notification as read. Idempotent — subsequent calls are no-ops.
    /// </summary>
    public void MarkAsRead()
    {
        if (IsRead)
        {
            return;
        }

        IsRead = true;
        ReadOn = DateTime.UtcNow;
    }
}
