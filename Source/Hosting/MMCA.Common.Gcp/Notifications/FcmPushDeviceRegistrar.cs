using FirebaseAdmin;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// Firebase Cloud Messaging implementation of <see cref="IPushDeviceRegistrar"/> (ADR-044). Each
/// installation is one Firestore document (where ownership lives: the same handover and
/// owner-scoped delete rules as the Notification Hubs and SNS registrars, decided inside a Firestore
/// transaction) plus its registration token's subscription to the broadcast topic.
/// <para>
/// Only FCM registration tokens are accepted. FCM delivers to iOS too, so an iOS client registers
/// the FCM token its Firebase SDK hands out (platform <c>fcmv1</c>); a raw APNs device token is
/// refused, since this adapter has no direct APNs sender.
/// </para>
/// </summary>
/// <param name="registry">The installation registry.</param>
/// <param name="messaging">The FCM client, for the topic subscriptions.</param>
/// <param name="options">The push settings.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class FcmPushDeviceRegistrar(
    IFcmInstallationRegistry registry,
    IFcmMessagingClient messaging,
    IOptions<FcmNativePushSettings> options,
    ILogger<FcmPushDeviceRegistrar> logger) : IPushDeviceRegistrar
{
    private string Topic => options.Value.BroadcastTopic!;

    /// <inheritdoc />
    public async Task<Result> UpsertAsync(UserIdentifierType userId, DeviceInstallationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var platform = request.Platform.ToUpperInvariant();
        if (string.Equals(platform, "APNS", StringComparison.Ordinal))
        {
            return Result.Failure(Error.Validation(
                code: "PushDevice.UnsupportedPlatform",
                message: $"Platform '{DeviceInstallationRequest.ApnsPlatform}' is not supported on this deployment, which delivers through Firebase Cloud Messaging: iOS clients must register an FCM registration token with platform '{DeviceInstallationRequest.FcmV1Platform}'.",
                source: nameof(FcmPushDeviceRegistrar)));
        }

        if (!string.Equals(platform, "FCMV1", StringComparison.Ordinal))
        {
            return Result.Failure(Error.Validation(
                code: "PushDevice.UnsupportedPlatform",
                message: $"Platform must be '{DeviceInstallationRequest.FcmV1Platform}' (an FCM registration token, on Android and iOS alike).",
                source: nameof(FcmPushDeviceRegistrar)));
        }

        try
        {
            return await RegisterAsync(FcmInstallation.UserKey(userId), request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RpcException or FirebaseException)
        {
            logger.LogError(ex, "Device installation upsert failed");
            return UpsertFailed();
        }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(UserIdentifierType userId, string installationId, CancellationToken cancellationToken = default)
    {
        var owner = FcmInstallation.UserKey(userId);
        try
        {
            var existing = await registry.GetAsync(installationId, cancellationToken).ConfigureAwait(false);
            if (existing is null || !string.Equals(existing.UserId, owner, StringComparison.Ordinal))
            {
                // Unknown, or someone else's installation: reported as success either way. See the
                // interface remarks on the existence oracle.
                return Result.Success();
            }

            // Subscription first, document last: a failure part-way leaves the document, so the
            // client's retry finds it and finishes the job, rather than a token still subscribed to
            // broadcasts with no document left to find it by. The delete is conditional on the
            // owner, so a document handed over in the meantime survives.
            var refused = await messaging.UnsubscribeFromTopicAsync(existing.PushChannel, Topic, cancellationToken).ConfigureAwait(false);
            if (refused is not null)
            {
                LogUnsubscribeRefused(logger, installationId, refused);
            }

            await registry.DeleteOwnedAsync(installationId, owner, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex) when (ex is RpcException or FirebaseException)
        {
            logger.LogError(ex, "Device installation delete failed");
            return Result.Failure(Error.Failure(
                code: "PushDevice.DeleteFailed",
                message: "The device could not be unregistered from push notifications.",
                source: nameof(FcmPushDeviceRegistrar)));
        }
    }

    private static Result UpsertFailed() =>
        Result.Failure(Error.Failure(
            code: "PushDevice.UpsertFailed",
            message: "The device could not be registered for push notifications.",
            source: nameof(FcmPushDeviceRegistrar)));

    private async Task<Result> RegisterAsync(string owner, DeviceInstallationRequest request, CancellationToken cancellationToken)
    {
        var installation = new FcmInstallation(request.InstallationId, owner, DeviceInstallationRequest.FcmV1Platform, request.PushChannel);

        // The ownership decision comes first and nothing is subscribed until it is made, so another
        // device's installation stays exactly as it is.
        var (claimed, previousPushChannel) = await registry.TryClaimAsync(installation, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            return Result.Failure(Error.Conflict(
                code: "PushDevice.InstallationConflict",
                message: "The installation id is registered to another device. Generate a new installation id and retry.",
                source: nameof(FcmPushDeviceRegistrar)));
        }

        // Subscribed on every upsert, not only for a new token: FCM treats a repeat as a no-op, and a
        // retry after a failed subscription (the document already written) must still finish the job.
        var refused = await messaging.SubscribeToTopicAsync(request.PushChannel, Topic, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
        {
            LogSubscribeRefused(logger, request.InstallationId, refused);
            return UpsertFailed();
        }

        if (previousPushChannel is not null && !string.Equals(previousPushChannel, request.PushChannel, StringComparison.Ordinal))
        {
            // The installation moved to a new token: the old one no longer has a document, so it must
            // not keep receiving broadcasts.
            await UnsubscribeReplacedAsync(previousPushChannel, request.InstallationId, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>Best-effort removal of a replaced token's topic subscription.</summary>
    private async Task UnsubscribeReplacedAsync(string pushChannel, string installationId, CancellationToken cancellationToken)
    {
        try
        {
            var refused = await messaging.UnsubscribeFromTopicAsync(pushChannel, Topic, cancellationToken).ConfigureAwait(false);
            if (refused is not null)
            {
                LogUnsubscribeRefused(logger, installationId, refused);
            }
        }
        catch (FirebaseException ex)
        {
            // The registration outcome stands; a stale subscription is waste, not a correctness problem.
            LogUnsubscribeReplacedFailed(logger, ex, installationId);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "FCM refused to subscribe the token of installation {InstallationId} to the broadcast topic ({Reason})")]
    private static partial void LogSubscribeRefused(ILogger logger, string installationId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "FCM refused to unsubscribe a token of installation {InstallationId} from the broadcast topic ({Reason}); a token FCM no longer knows receives nothing either way")]
    private static partial void LogUnsubscribeRefused(ILogger logger, string installationId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unsubscribing the replaced FCM token of installation {InstallationId} failed")]
    private static partial void LogUnsubscribeReplacedFailed(ILogger logger, Exception exception, string installationId);
}
