namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkRead;

/// <summary>Command to mark a single notification as read for the current user.</summary>
/// <param name="NotificationId">The user notification identifier to mark as read.</param>
/// <param name="UserId">The authenticated user's identifier (ownership verification).</param>
public sealed record MarkNotificationReadCommand(
    UserNotificationIdentifierType NotificationId,
    UserIdentifierType UserId);
