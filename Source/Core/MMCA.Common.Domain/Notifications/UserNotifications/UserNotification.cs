using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

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
    /// <returns>A <see cref="Result{T}"/> containing the created notification.</returns>
    public static Result<UserNotification> Create(
        UserIdentifierType userId,
        PushNotificationIdentifierType pushNotificationId) =>
        Result.Success(new UserNotification(userId, pushNotificationId) { Id = default });

    /// <summary>
    /// Marks the notification as read at the supplied UTC timestamp. Idempotent — subsequent
    /// calls are no-ops, preserving the original read time.
    /// </summary>
    /// <param name="readOnUtc">
    /// The UTC instant the notification was read. Supplied by the caller (from an injected
    /// <see cref="TimeProvider"/>) so the domain stays free of ambient clock access and the
    /// behavior is deterministically testable.
    /// </param>
    public void MarkAsRead(DateTime readOnUtc)
    {
        if (IsRead)
        {
            return;
        }

        IsRead = true;
        ReadOn = readOnUtc;
    }
}
