using Grpc.Core;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// Firebase Cloud Messaging implementation of <see cref="IPushDeviceRegistrar"/> (ADR-044). Each
/// installation is one Firestore document, which is where ownership lives: the same handover and
/// owner-scoped delete rules as the Notification Hubs and SNS registrars, decided inside a Firestore
/// transaction. A send resolves the user's registration tokens from these documents, so the document
/// is the whole registration.
/// <para>
/// Only FCM registration tokens are accepted. FCM delivers to iOS too, so an iOS client registers
/// the FCM token its Firebase SDK hands out (platform <c>fcmv1</c>); a raw APNs device token is
/// refused, since this adapter has no direct APNs sender.
/// </para>
/// </summary>
/// <param name="registry">The installation registry.</param>
/// <param name="logger">The logger.</param>
internal sealed class FcmPushDeviceRegistrar(
    IFcmInstallationRegistry registry,
    ILogger<FcmPushDeviceRegistrar> logger) : IPushDeviceRegistrar
{
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

        var installation = new FcmInstallation(request.InstallationId, FcmInstallation.UserKey(userId), DeviceInstallationRequest.FcmV1Platform, request.PushChannel);
        try
        {
            // The ownership decision is the whole registration, so another device's installation
            // stays exactly as it is when the claim fails.
            var (claimed, _) = await registry.TryClaimAsync(installation, cancellationToken).ConfigureAwait(false);
            return claimed
                ? Result.Success()
                : Result.Failure(Error.Conflict(
                    code: "PushDevice.InstallationConflict",
                    message: "The installation id is registered to another device. Generate a new installation id and retry.",
                    source: nameof(FcmPushDeviceRegistrar)));
        }
        catch (RpcException ex)
        {
            logger.LogError(ex, "Device installation upsert failed");
            return Result.Failure(Error.Failure(
                code: "PushDevice.UpsertFailed",
                message: "The device could not be registered for push notifications.",
                source: nameof(FcmPushDeviceRegistrar)));
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

            // Conditional on the owner, so a document handed over in the meantime survives.
            await registry.DeleteOwnedAsync(installationId, owner, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (RpcException ex)
        {
            logger.LogError(ex, "Device installation delete failed");
            return Result.Failure(Error.Failure(
                code: "PushDevice.DeleteFailed",
                message: "The device could not be unregistered from push notifications.",
                source: nameof(FcmPushDeviceRegistrar)));
        }
    }
}
