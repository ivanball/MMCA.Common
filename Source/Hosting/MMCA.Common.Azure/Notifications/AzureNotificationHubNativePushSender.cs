using Microsoft.Azure.NotificationHubs;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Azure.Notifications;

/// <summary>
/// Azure Notification Hubs implementation of <see cref="INativePushSender"/> (ADR-044). Sends
/// the FCM v1 and APNs native payloads per call; user-targeted sends resolve installations via
/// <c>user:{id}</c> tags in OR-chunks of <see cref="NativePushUserTags.MaxTagsPerExpression"/>
/// (the hub's tag-expression cap).
/// <para>
/// Every chunk is attempted on every platform: a failing send (an APNs rejection, say) does not
/// stop the later chunks or the other platform. The hub reports per send only, never per device,
/// so the outcome counts batches (<see cref="NativePushDeliveryUnit.Batch"/>): one per chunk per
/// platform, Android for FCM v1 and Apple for APNs. A failure is counted and the first one is
/// returned in <see cref="NativePushOutcome.FirstFailure"/> rather than thrown. The caller's own
/// cancellation stops the loop and propagates.
/// </para>
/// </summary>
public sealed partial class AzureNotificationHubNativePushSender(
    INotificationHubClient hubClient,
    ILogger<AzureNotificationHubNativePushSender> logger) : INativePushSender
{
    private const string FcmPlatform = "fcmv1";
    private const string ApnsPlatform = "apns";

    /// <inheritdoc />
    public async Task<NativePushOutcome> SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        var fcmPayload = NativePushPayloads.BuildFcmV1Payload(title, body, metadata);
        var apnsPayload = NativePushPayloads.BuildApnsPayload(title, body, metadata);
        var androidSent = 0;
        var androidFailed = 0;
        var appleSent = 0;
        var appleFailed = 0;
        Exception? firstFailure = null;

        foreach (var tagExpression in NativePushUserTags.BuildUserTagExpressions(userIds))
        {
            var androidError = await TrySendAsync(new FcmV1Notification(fcmPayload), FcmPlatform, tagExpression, cancellationToken).ConfigureAwait(false);
            if (androidError is null)
            {
                androidSent++;
            }
            else
            {
                androidFailed++;
                firstFailure ??= androidError;
            }

            var appleError = await TrySendAsync(new AppleNotification(apnsPayload), ApnsPlatform, tagExpression, cancellationToken).ConfigureAwait(false);
            if (appleError is null)
            {
                appleSent++;
            }
            else
            {
                appleFailed++;
                firstFailure ??= appleError;
            }
        }

        LogNativePushSent(logger, title, androidSent, androidSent + androidFailed, appleSent, appleSent + appleFailed);
        return NativePushOutcome.From(NativePushDeliveryUnit.Batch, androidSent, androidFailed, appleSent, appleFailed, firstFailure);
    }

    /// <summary>Sends one batch on one platform.</summary>
    /// <returns>Null on success, otherwise the failure (never the caller's own cancellation, which propagates).</returns>
    private async Task<Exception?> TrySendAsync(Notification notification, string platform, string tagExpression, CancellationToken cancellationToken)
    {
        try
        {
            await hubClient.SendNotificationAsync(notification, tagExpression, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Counted, not swallowed: the caller gets the first failure in the outcome.
            LogPlatformSendFailed(logger, platform, ex.GetType().Name);
            return ex;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Native push \"{Title}\" handed to the notification hub: Android {AndroidSent} of {AndroidAttempted} batches, Apple {AppleSent} of {AppleAttempted} batches")]
    private static partial void LogNativePushSent(ILogger logger, string title, int androidSent, int androidAttempted, int appleSent, int appleAttempted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Platform} native push send to the notification hub failed with {ExceptionType}; the remaining chunks and platforms still send")]
    private static partial void LogPlatformSendFailed(ILogger logger, string platform, string exceptionType);
}
