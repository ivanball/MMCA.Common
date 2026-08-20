using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.UserNotifications;

namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// UI service contract for the notification inbox.
/// </summary>
public interface INotificationInboxUIService
{
    /// <summary>Gets the current user's notification inbox with pagination.</summary>
    Task<PagedCollectionResult<UserNotificationDTO>?> GetInboxAsync(int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of unread notifications for the current user, or <see langword="null"/> when
    /// the count could not be established (expired session, transient failure). Callers must treat
    /// <see langword="null"/> as "unknown" and leave the displayed count untouched: reporting zero
    /// would erase a badge that a real-time push had just incremented.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int?> GetUnreadCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks a single notification as read.</summary>
    Task MarkReadAsync(UserNotificationIdentifierType id, CancellationToken cancellationToken = default);

    /// <summary>Marks all notifications as read.</summary>
    Task MarkAllReadAsync(CancellationToken cancellationToken = default);
}
