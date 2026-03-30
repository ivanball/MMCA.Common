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

    /// <summary>Gets the count of unread notifications for the current user.</summary>
    Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks a single notification as read.</summary>
    Task MarkReadAsync(UserNotificationIdentifierType id, CancellationToken cancellationToken = default);

    /// <summary>Marks all notifications as read.</summary>
    Task MarkAllReadAsync(CancellationToken cancellationToken = default);
}
