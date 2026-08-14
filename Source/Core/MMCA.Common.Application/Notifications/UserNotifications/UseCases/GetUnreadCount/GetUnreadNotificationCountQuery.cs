namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetUnreadCount;

/// <summary>Query to retrieve the number of unread notifications for a user.</summary>
/// <param name="UserId">The authenticated user's identifier.</param>
/// <param name="ScopeKey">
/// Optional scope key. Null (the default) is the legacy count over every notification; a scope
/// counts only the notifications carrying that scope plus the unscoped ones.
/// </param>
public sealed record GetUnreadNotificationCountQuery(
    UserIdentifierType UserId,
    string? ScopeKey = null);
