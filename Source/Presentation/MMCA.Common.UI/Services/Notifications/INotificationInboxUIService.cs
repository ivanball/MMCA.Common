using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.UserNotifications;

namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// UI service contract for the notification inbox. Every member returns a <see cref="Result"/>
/// carrying the API's own errors, so a caller can tell a real answer apart from a failure without
/// catching anything.
/// </summary>
public interface INotificationInboxUIService
{
    /// <summary>Gets the current user's notification inbox with pagination.</summary>
    Task<Result<PagedCollectionResult<UserNotificationDTO>>> GetInboxAsync(int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of unread notifications for the current user. A failure means the count could
    /// not be established (expired session, transient failure) and must be treated as "unknown":
    /// callers leave the displayed count untouched, because reporting zero would erase a badge that
    /// a real-time push had just incremented.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<Result<int>> GetUnreadCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks a single notification as read.</summary>
    Task<Result> MarkReadAsync(UserNotificationIdentifierType id, CancellationToken cancellationToken = default);

    /// <summary>Marks all notifications as read.</summary>
    Task<Result> MarkAllReadAsync(CancellationToken cancellationToken = default);
}
