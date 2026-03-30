namespace MMCA.Common.Application.Notifications.PushNotifications.UseCases.GetHistory;

/// <summary>Query to retrieve push notification history.</summary>
/// <param name="PageNumber">Page number (1-based).</param>
/// <param name="PageSize">Items per page (max 500).</param>
public sealed record GetNotificationHistoryQuery(
    int PageNumber = 1,
    int PageSize = 10);
