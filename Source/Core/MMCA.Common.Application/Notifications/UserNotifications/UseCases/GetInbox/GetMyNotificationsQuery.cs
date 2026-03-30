namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetInbox;

/// <summary>Query to retrieve a user's notification inbox with pagination.</summary>
/// <param name="UserId">The authenticated user's identifier.</param>
/// <param name="PageNumber">Page number (1-based).</param>
/// <param name="PageSize">Items per page (max 500).</param>
public sealed record GetMyNotificationsQuery(
    UserIdentifierType UserId,
    int PageNumber = 1,
    int PageSize = 20);
