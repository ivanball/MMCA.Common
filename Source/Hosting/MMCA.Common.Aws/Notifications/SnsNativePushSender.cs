using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Aws.Notifications;

/// <summary>
/// SNS mobile push implementation of <see cref="INativePushSender"/> (ADR-044). A user-targeted send
/// resolves each user's endpoints through the registry's <c>UserId</c> index and publishes once per
/// endpoint, in the <see cref="SnsPushMessage"/> structure, so each endpoint receives its own
/// platform's payload.
/// <para>
/// Best-effort, like the channel itself: one failing endpoint or user lookup is logged and the rest
/// still receive. An endpoint SNS reports disabled (the platform rejected its token) or missing is
/// removed together with its row, so a dead device stops costing a publish on every send.
/// </para>
/// <para>
/// One publish per ENDPOINT, not per row: two installation ids holding the same device token share
/// one SNS endpoint (the registrar creates endpoints by token), so a user's rows are grouped by
/// <see cref="SnsInstallation.EndpointArn"/> and the device is published to once. A stale endpoint
/// removes every row of its group, as the FCM adapter's per-token grouping does.
/// </para>
/// <para>
/// The outcome counts endpoints (<see cref="NativePushDeliveryUnit.Endpoint"/>), on the platform of
/// the endpoint's platform application: <c>fcmv1</c> is Android, <c>apns</c> is Apple. A disabled
/// or missing endpoint counts as failed (its device was not reached; a bad APNs credential shows up
/// this way), and a failed user lookup is returned as a failure without a platform count.
/// </para>
/// </summary>
/// <param name="registry">The installation registry.</param>
/// <param name="sns">The SNS client.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class SnsNativePushSender(
    SnsInstallationRegistry registry,
    IAmazonSimpleNotificationService sns,
    ILogger<SnsNativePushSender> logger) : INativePushSender
{
    /// <inheritdoc />
    public async Task<NativePushOutcome> SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var message = SnsPushMessage.Build(title, body, metadata);
        var tally = new EndpointTally();

        foreach (var userKey in userIds.Select(SnsInstallationRegistry.UserKey).Distinct(StringComparer.Ordinal))
        {
            IReadOnlyList<SnsInstallation> installations;
            try
            {
                installations = await registry.QueryByUserAsync(userKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AmazonServiceException || IsTransportFailure(ex, cancellationToken))
            {
                LogInstallationLookupFailed(logger, ex, userKey);

                // The user's devices are unknown, so no platform is counted; the failure still is.
                tally.Fail(ex);
                continue;
            }

            foreach (var endpoint in installations.GroupBy(i => i.EndpointArn, StringComparer.Ordinal))
            {
                var failure = await TryPublishAsync(endpoint, message, cancellationToken).ConfigureAwait(false);

                // The platform is the endpoint's platform application: every row of the group
                // registered the same token, so it registered on the same platform.
                tally.Count(endpoint.First().Platform, failure);
            }
        }

        LogNativePushSent(logger, title, tally.Delivered);
        return tally.ToOutcome();
    }

    /// <summary>Publishes to one endpoint.</summary>
    /// <returns>Null on success, otherwise the failure (a stale endpoint is removed and still counts as a failure).</returns>
    private async Task<Exception?> TryPublishAsync(IGrouping<string, SnsInstallation> endpoint, string message, CancellationToken cancellationToken)
    {
        try
        {
            await sns.PublishAsync(
                new PublishRequest
                {
                    TargetArn = endpoint.Key,
                    Message = message,
                    MessageStructure = SnsPushMessage.Structure,
                },
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is EndpointDisabledException or NotFoundException)
        {
            await RemoveStaleAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return ex;
        }
        catch (Exception ex) when (ex is AmazonServiceException || IsTransportFailure(ex, cancellationToken))
        {
            LogPublishFailed(logger, ex, endpoint.Key);
            return ex;
        }
    }

    /// <summary>
    /// Removes a stale endpoint once and then every row that points at it, so no surviving row is
    /// left to fail its own publish later.
    /// </summary>
    private async Task RemoveStaleAsync(IGrouping<string, SnsInstallation> endpoint, CancellationToken cancellationToken)
    {
        LogStaleEndpointRemoved(logger, endpoint.Key);
        try
        {
            await registry.RemoveEndpointAsync(endpoint.Key, cancellationToken).ConfigureAwait(false);
            foreach (var installation in endpoint)
            {
                await registry.DeleteIfEndpointAsync(installation.InstallationId, endpoint.Key, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is AmazonServiceException || IsTransportFailure(ex, cancellationToken))
        {
            LogStaleEndpointRemovalFailed(logger, ex, endpoint.Key);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Native push \"{Title}\" published to {EndpointCount} SNS endpoints")]
    private static partial void LogNativePushSent(ILogger logger, string title, int endpointCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Looking up the push installations of user {UserId} failed; that user is skipped")]
    private static partial void LogInstallationLookupFailed(ILogger logger, Exception exception, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Native push publish to SNS endpoint {EndpointArn} failed; the remaining endpoints still receive")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, string endpointArn);

    [LoggerMessage(Level = LogLevel.Information, Message = "SNS endpoint {EndpointArn} is disabled or gone; removing it and its installation")]
    private static partial void LogStaleEndpointRemoved(ILogger logger, string endpointArn);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing the stale SNS endpoint {EndpointArn} failed")]
    private static partial void LogStaleEndpointRemovalFailed(ILogger logger, Exception exception, string endpointArn);

    /// <summary>
    /// A failure that never produced a service response: a connect or DNS failure, a dropped
    /// connection, or a timeout after the client's own retries (surfaced as a <see cref="TimeoutException"/>
    /// or as an <see cref="OperationCanceledException"/> the caller did not request). The AWS SDK lets these
    /// escape unwrapped, so they are handled like a service failure; the caller's own cancellation
    /// is never one: once it is requested, every exception keeps propagating. Same predicate as <c>S3FileStorageService</c>.
    /// </summary>
    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && exception is HttpRequestException or IOException or TimeoutException or OperationCanceledException;

    /// <summary>
    /// The per-platform endpoint counts of one send. An endpoint on a platform this adapter does not
    /// register (neither <c>fcmv1</c> nor <c>apns</c>) is not counted; its failure still is.
    /// </summary>
    private sealed class EndpointTally
    {
        private int _androidSent;
        private int _androidFailed;
        private int _appleSent;
        private int _appleFailed;
        private Exception? _firstFailure;

        public int Delivered => _androidSent + _appleSent;

        public void Fail(Exception failure) => _firstFailure ??= failure;

        public void Count(string platform, Exception? failure)
        {
            if (failure is not null)
            {
                Fail(failure);
            }

            if (string.Equals(platform, DeviceInstallationRequest.FcmV1Platform, StringComparison.Ordinal))
            {
                if (failure is null)
                {
                    _androidSent++;
                }
                else
                {
                    _androidFailed++;
                }
            }
            else if (string.Equals(platform, DeviceInstallationRequest.ApnsPlatform, StringComparison.Ordinal))
            {
                if (failure is null)
                {
                    _appleSent++;
                }
                else
                {
                    _appleFailed++;
                }
            }
        }

        public NativePushOutcome ToOutcome() =>
            NativePushOutcome.From(NativePushDeliveryUnit.Endpoint, _androidSent, _androidFailed, _appleSent, _appleFailed, _firstFailure);
    }
}
