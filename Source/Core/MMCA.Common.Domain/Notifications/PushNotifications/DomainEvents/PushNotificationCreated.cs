using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Domain.Notifications.PushNotifications.DomainEvents;

/// <summary>
/// Domain event raised when a push notification is created.
/// </summary>
/// <param name="NotificationId">The notification identifier (default until persisted).</param>
/// <param name="Title">The notification title.</param>
/// <param name="RecipientCount">The number of targeted recipients.</param>
public sealed record class PushNotificationCreated(
    PushNotificationIdentifierType NotificationId,
    string Title,
    int RecipientCount)
    : BaseDomainEvent;
