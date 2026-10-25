using FirebaseAdmin;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// Firebase Cloud Messaging implementation of <see cref="INativePushSender"/> (ADR-044). A
/// user-targeted send resolves the users' registration tokens through the Firestore registry and
/// sends one message per distinct token with <c>SendEachAsync</c>, in chunks of FirebaseAdmin's
/// per-call maximum; a broadcast is one message to the topic every token subscribes to. Both carry
/// the <see cref="FcmPushMessage"/> payload, so an Android and an iOS device each receive their own
/// platform's block.
/// <para>
/// Best-effort, like the channel itself: a failing token, chunk or user lookup is logged and the
/// rest still receive. A token FCM reports dead (unregistered, or rejected as not a registration
/// token) is pruned from the registry, so a dead device stops costing a send every time; the
/// document is deleted only while it still holds that token. A broadcast failure propagates.
/// </para>
/// </summary>
/// <param name="registry">The installation registry.</param>
/// <param name="messaging">The FCM client.</param>
/// <param name="options">The push settings.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class FcmNativePushSender(
    IFcmInstallationRegistry registry,
    IFcmMessagingClient messaging,
    IOptions<FcmNativePushSettings> options,
    ILogger<FcmNativePushSender> logger) : INativePushSender
{
    /// <inheritdoc />
    public async Task SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        List<FcmInstallation> installations = [];
        foreach (var batch in userIds.Select(FcmInstallation.UserKey).Distinct(StringComparer.Ordinal).Chunk(FirestoreInstallationRegistry.MaxUsersPerQuery))
        {
            try
            {
                installations.AddRange(await registry.QueryByUsersAsync(batch, cancellationToken).ConfigureAwait(false));
            }
            catch (RpcException ex)
            {
                LogInstallationLookupFailed(logger, ex, batch.Length);
            }
        }

        // One message per token: two installation ids holding the same token are one device.
        var byToken = installations
            .GroupBy(installation => installation.PushChannel, StringComparer.Ordinal)
            .ToList();

        var delivered = 0;
        foreach (var chunk in byToken.Chunk(IFcmMessagingClient.MaxMessagesPerSend))
        {
            var messages = chunk.Select(group => FcmPushMessage.ForToken(group.Key, title, body, metadata)).ToList();
            IReadOnlyList<FcmSendOutcome> outcomes;
            try
            {
                outcomes = await messaging.SendEachAsync(messages, cancellationToken).ConfigureAwait(false);
            }
            catch (FirebaseException ex)
            {
                LogChunkFailed(logger, ex, messages.Count);
                continue;
            }

            for (var i = 0; i < chunk.Length && i < outcomes.Count; i++)
            {
                var outcome = outcomes[i];
                if (outcome.IsSuccess)
                {
                    delivered++;
                }
                else if (outcome.IsStaleToken)
                {
                    await PruneAsync(chunk[i], cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    LogSendFailed(logger, outcome.Exception, outcome.ErrorCode?.ToString() ?? "none", chunk[i].First().InstallationId);
                }
            }
        }

        LogNativePushSent(logger, title, delivered);
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        await messaging.SendAsync(FcmPushMessage.ForTopic(options.Value.BroadcastTopic!, title, body, metadata), cancellationToken).ConfigureAwait(false);

        LogNativePushBroadcast(logger, title);
    }

    private async Task PruneAsync(IGrouping<string, FcmInstallation> installations, CancellationToken cancellationToken)
    {
        foreach (var installation in installations)
        {
            LogStaleTokenPruned(logger, installation.InstallationId);
            try
            {
                await registry.DeleteIfPushChannelAsync(installation.InstallationId, installation.PushChannel, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                LogStaleTokenPruneFailed(logger, ex, installation.InstallationId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Native push \"{Title}\" sent to {TokenCount} FCM registration tokens")]
    private static partial void LogNativePushSent(ILogger logger, string title, int tokenCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Native push \"{Title}\" sent to the FCM broadcast topic")]
    private static partial void LogNativePushBroadcast(ILogger logger, string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Looking up the push installations of {UserCount} users failed; those users are skipped")]
    private static partial void LogInstallationLookupFailed(ILogger logger, Exception exception, int userCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An FCM send of {MessageCount} messages failed; the remaining chunks still send")]
    private static partial void LogChunkFailed(ILogger logger, Exception exception, int messageCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Native push to installation {InstallationId} failed with FCM error {ErrorCode}; the remaining tokens still receive")]
    private static partial void LogSendFailed(ILogger logger, Exception? exception, string errorCode, string installationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "The FCM token of installation {InstallationId} is dead; removing the installation")]
    private static partial void LogStaleTokenPruned(ILogger logger, string installationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing the installation {InstallationId} with a dead FCM token failed")]
    private static partial void LogStaleTokenPruneFailed(ILogger logger, Exception exception, string installationId);
}
