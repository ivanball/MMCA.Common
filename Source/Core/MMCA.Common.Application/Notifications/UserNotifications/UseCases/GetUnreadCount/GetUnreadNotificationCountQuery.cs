namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetUnreadCount;

/// <summary>Query to retrieve the number of unread notifications for a user.</summary>
/// <param name="UserId">The authenticated user's identifier.</param>
public sealed record GetUnreadNotificationCountQuery(UserIdentifierType UserId);
