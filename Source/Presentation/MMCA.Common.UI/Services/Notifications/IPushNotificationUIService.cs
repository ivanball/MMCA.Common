using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// UI service contract for push notification operations.
/// </summary>
public interface IPushNotificationUIService
{
    /// <summary>Sends a push notification to all recipients.</summary>
    Task<PushNotificationDTO?> SendAsync(SendPushNotificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets paginated notification history.</summary>
    Task<PagedCollectionResult<PushNotificationDTO>?> GetHistoryAsync(int pageNumber = 1, int pageSize = 10, CancellationToken cancellationToken = default);
}
