using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;

namespace MMCA.Common.Aws.Notifications;

/// <summary>
/// SNS mobile push implementation of <see cref="INativePushSender"/> (ADR-044). A user-targeted send
/// resolves each user's endpoints through the registry's <c>UserId</c> index and publishes once per
/// endpoint; a broadcast is one publish to the topic every endpoint subscribes to. Both publish the
/// <see cref="SnsPushMessage"/> structure, so each endpoint receives its own platform's payload.
/// <para>
/// Best-effort, like the channel itself: one failing endpoint or user lookup is logged and the rest
/// still receive. An endpoint SNS reports disabled (the platform rejected its token) or missing is
/// removed together with its row, so a dead device stops costing a publish on every send.
/// </para>
/// </summary>
/// <param name="registry">The installation registry.</param>
/// <param name="sns">The SNS client.</param>
/// <param name="options">The push settings.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class SnsNativePushSender(
    SnsInstallationRegistry registry,
    IAmazonSimpleNotificationService sns,
    IOptions<SnsNativePushSettings> options,
    ILogger<SnsNativePushSender> logger) : INativePushSender
{
    /// <inheritdoc />
    public async Task SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var message = SnsPushMessage.Build(title, body, metadata);
        var delivered = 0;

        foreach (var userKey in userIds.Select(SnsInstallationRegistry.UserKey).Distinct(StringComparer.Ordinal))
        {
            IReadOnlyList<SnsInstallation> installations;
            try
            {
                installations = await registry.QueryByUserAsync(userKey, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonServiceException ex)
            {
                LogInstallationLookupFailed(logger, ex, userKey);
                continue;
            }

            foreach (var installation in installations)
            {
                if (await TryPublishAsync(installation, message, cancellationToken).ConfigureAwait(false))
                {
                    delivered++;
                }
            }
        }

        LogNativePushSent(logger, title, delivered);
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        await sns.PublishAsync(
            new PublishRequest
            {
                TopicArn = options.Value.BroadcastTopicArn,
                Message = SnsPushMessage.Build(title, body, metadata),
                MessageStructure = SnsPushMessage.Structure,
            },
            cancellationToken).ConfigureAwait(false);

        LogNativePushBroadcast(logger, title);
    }

    private async Task<bool> TryPublishAsync(SnsInstallation installation, string message, CancellationToken cancellationToken)
    {
        try
        {
            await sns.PublishAsync(
                new PublishRequest
                {
                    TargetArn = installation.EndpointArn,
                    Message = message,
                    MessageStructure = SnsPushMessage.Structure,
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is EndpointDisabledException or NotFoundException)
        {
            await RemoveStaleAsync(installation, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (AmazonServiceException ex)
        {
            LogPublishFailed(logger, ex, installation.EndpointArn);
            return false;
        }
    }

    private async Task RemoveStaleAsync(SnsInstallation installation, CancellationToken cancellationToken)
    {
        LogStaleEndpointRemoved(logger, installation.EndpointArn);
        try
        {
            await registry.RemoveEndpointAsync(installation.EndpointArn, installation.SubscriptionArn, cancellationToken).ConfigureAwait(false);
            await registry.DeleteIfEndpointAsync(installation.InstallationId, installation.EndpointArn, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonServiceException ex)
        {
            LogStaleEndpointRemovalFailed(logger, ex, installation.EndpointArn);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Native push \"{Title}\" published to {EndpointCount} SNS endpoints")]
    private static partial void LogNativePushSent(ILogger logger, string title, int endpointCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Native push \"{Title}\" published to the SNS broadcast topic")]
    private static partial void LogNativePushBroadcast(ILogger logger, string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Looking up the push installations of user {UserId} failed; that user is skipped")]
    private static partial void LogInstallationLookupFailed(ILogger logger, Exception exception, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Native push publish to SNS endpoint {EndpointArn} failed; the remaining endpoints still receive")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, string endpointArn);

    [LoggerMessage(Level = LogLevel.Information, Message = "SNS endpoint {EndpointArn} is disabled or gone; removing it and its installation")]
    private static partial void LogStaleEndpointRemoved(ILogger logger, string endpointArn);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing the stale SNS endpoint {EndpointArn} failed")]
    private static partial void LogStaleEndpointRemovalFailed(ILogger logger, Exception exception, string endpointArn);
}
