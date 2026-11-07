using FirebaseAdmin;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// Firebase Cloud Messaging implementation of <see cref="INativePushSender"/> (ADR-044). A
/// user-targeted send resolves the users' registration tokens through the Firestore registry and
/// sends one message per distinct token with <c>SendEachAsync</c>, in chunks of FirebaseAdmin's
/// per-call maximum. Each message carries the <see cref="FcmPushMessage"/> payload, so an Android
/// and an iOS device each receive their own platform's block.
/// <para>
/// Best-effort, like the channel itself: a failing token, chunk or user lookup is logged and the
/// rest still receive. A token FCM reports dead (unregistered, or rejected as not a registration
/// token) is pruned from the registry, so a dead device stops costing a send every time; the
/// document is deleted only while it still holds that token.
/// </para>
/// <para>
/// The outcome carries the first failure but no per-platform counts: an installation records
/// platform <c>fcmv1</c> on Android and iOS alike, so this sender cannot tell which phones a token
/// reached. A dead token counts as a failure, since its device was not reached.
/// </para>
/// </summary>
/// <param name="registry">The installation registry.</param>
/// <param name="messaging">The FCM client.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class FcmNativePushSender(
    IFcmInstallationRegistry registry,
    IFcmMessagingClient messaging,
    ILogger<FcmNativePushSender> logger) : INativePushSender
{
    /// <inheritdoc />
    public async Task<NativePushOutcome> SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var firstFailure = new FirstFailure();
        var installations = await LookupAsync(userIds, firstFailure, cancellationToken).ConfigureAwait(false);

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
                firstFailure.Offer(ex);
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
                    firstFailure.Offer(outcome.Exception);
                    await PruneAsync(chunk[i], cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    firstFailure.Offer(outcome.Exception);
                    LogSendFailed(logger, outcome.Exception, outcome.ErrorCode?.ToString() ?? "none", chunk[i].First().InstallationId);
                }
            }
        }

        LogNativePushSent(logger, title, delivered);

        // No per-platform counts: an installation registers platform fcmv1 on Android and iOS alike
        // (FcmPushDeviceRegistrar), so a token does not say which phone it belongs to.
        return new NativePushOutcome { Unit = NativePushDeliveryUnit.Message, FirstFailure = firstFailure.Value };
    }

    /// <summary>Resolves the users' installations, 30 users per query; a failed query skips those users.</summary>
    private async Task<List<FcmInstallation>> LookupAsync(IEnumerable<UserIdentifierType> userIds, FirstFailure firstFailure, CancellationToken cancellationToken)
    {
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
                firstFailure.Offer(ex);
            }
        }

        return installations;
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

    /// <summary>The first failure of one send, kept as the outcome's <see cref="NativePushOutcome.FirstFailure"/>.</summary>
    private sealed class FirstFailure
    {
        public Exception? Value { get; private set; }

        public void Offer(Exception? failure) => Value ??= failure;
    }
}
