namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;

/// <summary>Command to mark all of a user's notifications as read.</summary>
/// <param name="UserId">The authenticated user's identifier.</param>
public sealed record MarkAllNotificationsReadCommand(UserIdentifierType UserId);
