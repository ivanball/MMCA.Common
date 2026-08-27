using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.UserNotifications;
using MMCA.Common.UI.Services.Notifications;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// Canned <see cref="INotificationInboxUIService"/> for the gallery so <c>NotificationBell</c> and the
/// notification inbox page render populated, real markup for the axe/render E2E scan — no backend.
/// </summary>
internal sealed class StubNotificationInboxUIService : INotificationInboxUIService
{
    public Task<Result<PagedCollectionResult<UserNotificationDTO>>> GetInboxAsync(
        int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        UserNotificationDTO[] items =
        [
            new()
            {
                Id = 1, PushNotificationId = 1, Title = "Welcome to MMCA", Body = "Your account is ready.",
                IsRead = false, SentOn = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                Id = 2, PushNotificationId = 2, Title = "Scheduled maintenance", Body = "Sunday 02:00 UTC.",
                IsRead = true, ReadOn = new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc),
                SentOn = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            },
        ];
        return Task.FromResult(Result.Success(
            new PagedCollectionResult<UserNotificationDTO>(items, new PaginationMetadata(items.Length, pageSize, pageNumber))));
    }

    public Task<Result<int>> GetUnreadCountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(3));

    public Task<Result> MarkReadAsync(UserNotificationIdentifierType id, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success());

    public Task<Result> MarkAllReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
