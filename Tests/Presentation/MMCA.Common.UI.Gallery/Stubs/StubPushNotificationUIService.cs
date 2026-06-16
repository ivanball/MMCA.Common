using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.UI.Services.Notifications;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// Canned <see cref="IPushNotificationUIService"/> for the gallery so the notification history/compose
/// pages render populated, real markup for the axe/render E2E scan — no backend.
/// </summary>
internal sealed class StubPushNotificationUIService : IPushNotificationUIService
{
    public Task<PushNotificationDTO?> SendAsync(
        SendPushNotificationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult<PushNotificationDTO?>(new PushNotificationDTO
        {
            Id = 99, Title = request.Title, Body = request.Body, SentByUserId = 1,
            RecipientCount = 42, Status = "Sent", CreatedOn = new DateTime(2026, 1, 4, 10, 0, 0, DateTimeKind.Utc),
        });

    public Task<PagedCollectionResult<PushNotificationDTO>?> GetHistoryAsync(
        int pageNumber = 1, int pageSize = 10, CancellationToken cancellationToken = default)
    {
        PushNotificationDTO[] items =
        [
            new()
            {
                Id = 1, Title = "Welcome to MMCA", Body = "Your account is ready.", SentByUserId = 1,
                RecipientCount = 128, Status = "Sent", CreatedOn = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                Id = 2, Title = "Scheduled maintenance", Body = "Sunday 02:00 UTC.", SentByUserId = 1,
                RecipientCount = 128, Status = "Failed", CreatedOn = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            },
        ];
        return Task.FromResult<PagedCollectionResult<PushNotificationDTO>?>(
            new PagedCollectionResult<PushNotificationDTO>(items, new PaginationMetadata(items.Length, pageSize, pageNumber)));
    }
}
