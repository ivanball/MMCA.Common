namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;

/// <summary>Command to mark all of a user's notifications as read.</summary>
/// <param name="UserId">The authenticated user's identifier.</param>
/// <param name="ScopeKey">
/// Optional scope key. Null (the default) marks every notification read; a scope marks only the
/// notifications carrying that scope plus the unscoped ones, so a scoped client never silently
/// clears rows it could not see.
/// </param>
public sealed record MarkAllNotificationsReadCommand(
    UserIdentifierType UserId,
    string? ScopeKey = null);
