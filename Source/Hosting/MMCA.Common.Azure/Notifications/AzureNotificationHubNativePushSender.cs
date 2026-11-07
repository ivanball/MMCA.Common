using System.Runtime.ExceptionServices;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Azure.Notifications;

/// <summary>
/// Azure Notification Hubs implementation of <see cref="INativePushSender"/> (ADR-044). Sends
/// the FCM v1 and APNs native payloads per call; user-targeted sends resolve installations via
/// <c>user:{id}</c> tags in OR-chunks of <see cref="NativePushUserTags.MaxTagsPerExpression"/>
/// (the hub's tag-expression cap). Callers treat this channel as best-effort: the send handler
/// wraps it in a non-fatal catch.
/// <para>
/// Every chunk is attempted on every platform: a failing send (an APNs rejection, say) does not
/// stop the later chunks or the other platform. The failures are collected and rethrown once the
/// loop ends, the single exception as itself and several as an <see cref="AggregateException"/>,
/// so the caller still sees and logs the failure. The caller's own cancellation stops the loop.
/// </para>
/// </summary>
public sealed partial class AzureNotificationHubNativePushSender(
    INotificationHubClient hubClient,
    ILogger<AzureNotificationHubNativePushSender> logger) : INativePushSender
{
    private const string FcmPlatform = "fcmv1";
    private const string ApnsPlatform = "apns";

    /// <inheritdoc />
    public async Task SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        var fcmPayload = NativePushPayloads.BuildFcmV1Payload(title, body, metadata);
        var apnsPayload = NativePushPayloads.BuildApnsPayload(title, body, metadata);
        List<Exception> failures = [];

        foreach (var tagExpression in NativePushUserTags.BuildUserTagExpressions(userIds))
        {
            await TrySendAsync(new FcmV1Notification(fcmPayload), FcmPlatform, tagExpression, failures, cancellationToken).ConfigureAwait(false);
            await TrySendAsync(new AppleNotification(apnsPayload), ApnsPlatform, tagExpression, failures, cancellationToken).ConfigureAwait(false);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures.Count > 1)
        {
            throw new AggregateException($"{failures.Count} native push sends to the notification hub failed.", failures);
        }

        LogNativePushSent(logger, title);
    }

    private async Task TrySendAsync(Notification notification, string platform, string tagExpression, List<Exception> failures, CancellationToken cancellationToken)
    {
        try
        {
            await hubClient.SendNotificationAsync(notification, tagExpression, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Collected, not swallowed: SendToUsersAsync rethrows after every chunk has been attempted.
            LogPlatformSendFailed(logger, platform, ex.GetType().Name);
            failures.Add(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Native push \"{Title}\" handed to the notification hub")]
    private static partial void LogNativePushSent(ILogger logger, string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Platform} native push send to the notification hub failed with {ExceptionType}; the remaining chunks and platforms still send")]
    private static partial void LogPlatformSendFailed(ILogger logger, string platform, string exceptionType);
}
