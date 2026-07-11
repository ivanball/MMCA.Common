using Microsoft.Azure.NotificationHubs;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Azure Notification Hubs implementation of <see cref="INativePushSender"/> (ADR-044). Sends
/// the FCM v1 and APNs native payloads per call; user-targeted sends resolve installations via
/// <c>user:{id}</c> tags in OR-chunks of <see cref="NativePushPayloads.MaxTagsPerExpression"/>
/// (the hub's tag-expression cap). Callers treat this channel as best-effort: the send handler
/// wraps it in a non-fatal catch.
/// </summary>
public sealed partial class AzureNotificationHubNativePushSender(
    INotificationHubClient hubClient,
    ILogger<AzureNotificationHubNativePushSender> logger) : INativePushSender
{
    /// <inheritdoc />
    public async Task SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        var fcmPayload = NativePushPayloads.BuildFcmV1Payload(title, body, metadata);
        var apnsPayload = NativePushPayloads.BuildApnsPayload(title, body, metadata);

        foreach (var tagExpression in NativePushPayloads.BuildUserTagExpressions(userIds))
        {
            await hubClient.SendNotificationAsync(new FcmV1Notification(fcmPayload), tagExpression, cancellationToken).ConfigureAwait(false);
            await hubClient.SendNotificationAsync(new AppleNotification(apnsPayload), tagExpression, cancellationToken).ConfigureAwait(false);
        }

        LogNativePushSent(logger, title);
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        await hubClient.SendNotificationAsync(new FcmV1Notification(NativePushPayloads.BuildFcmV1Payload(title, body, metadata)), cancellationToken).ConfigureAwait(false);
        await hubClient.SendNotificationAsync(new AppleNotification(NativePushPayloads.BuildApnsPayload(title, body, metadata)), cancellationToken).ConfigureAwait(false);

        LogNativePushSent(logger, title);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Native push \"{Title}\" handed to the notification hub")]
    private static partial void LogNativePushSent(ILogger logger, string title);
}
