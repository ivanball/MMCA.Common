using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// UI service contract for push notification operations. Every member returns a
/// <see cref="Result"/> carrying the API's own errors.
/// </summary>
public interface IPushNotificationUIService
{
    /// <summary>Sends a push notification to all recipients.</summary>
    Task<Result<PushNotificationDTO>> SendAsync(SendPushNotificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets paginated notification history.</summary>
    Task<Result<PagedCollectionResult<PushNotificationDTO>>> GetHistoryAsync(int pageNumber = 1, int pageSize = 10, CancellationToken cancellationToken = default);
}
