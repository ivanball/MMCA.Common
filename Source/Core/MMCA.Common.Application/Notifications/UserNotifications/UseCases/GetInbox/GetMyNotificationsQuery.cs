namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetInbox;

/// <summary>Query to retrieve a user's notification inbox with pagination.</summary>
/// <param name="UserId">The authenticated user's identifier.</param>
/// <param name="PageNumber">Page number (1-based).</param>
/// <param name="PageSize">Items per page (max 500).</param>
/// <param name="ScopeKey">
/// Optional scope key. Null (the default) is the legacy read: every notification is returned.
/// A scope narrows the inbox to the notifications carrying that scope plus the unscoped ones.
/// </param>
public sealed record GetMyNotificationsQuery(
    UserIdentifierType UserId,
    int PageNumber = 1,
    int PageSize = 20,
    string? ScopeKey = null);
