using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Hubs;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Sends push notifications to connected clients via SignalR. Uses <see cref="IHubContext{THub}"/>
/// to send messages without requiring a direct hub connection. Batches large user lists to avoid
/// overwhelming the SignalR connection manager.
/// </summary>
public sealed class SignalRPushNotificationSender(IHubContext<NotificationHub> hubContext) : IPushNotificationSender
{
    private const int BatchSize = 100;

    /// <inheritdoc />
    public async Task SendToUserAsync(UserIdentifierType userId, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
        await hubContext.Clients
            .User(userId.ToString(CultureInfo.InvariantCulture))
            .SendAsync(NotificationHub.ReceiveNotificationMethod, title, body, metadata, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        foreach (IReadOnlyList<string> batch in BatchUserIds(userIds))
        {
            await hubContext.Clients
                .Users(batch)
                .SendAsync(NotificationHub.ReceiveNotificationMethod, title, body, metadata, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
        await hubContext.Clients.All
            .SendAsync(NotificationHub.ReceiveNotificationMethod, title, body, metadata, cancellationToken)
            .ConfigureAwait(false);

    private static IEnumerable<IReadOnlyList<string>> BatchUserIds(IEnumerable<UserIdentifierType> userIds)
    {
        var batch = new List<string>(BatchSize);
        foreach (UserIdentifierType userId in userIds)
        {
            batch.Add(userId.ToString(CultureInfo.InvariantCulture));
            if (batch.Count >= BatchSize)
            {
                yield return batch;
                batch = new List<string>(BatchSize);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}
