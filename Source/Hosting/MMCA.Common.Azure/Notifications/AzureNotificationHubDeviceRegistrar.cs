using Microsoft.Azure.NotificationHubs;
using Microsoft.Azure.NotificationHubs.Messaging;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Azure.Notifications;

/// <summary>
/// Azure Notification Hubs implementation of <see cref="IPushDeviceRegistrar"/> (ADR-044).
/// Uses the installation model (client-owned stable ids, full upsert semantics) and stamps
/// every installation with its owner's <c>user:{id}</c> tag so sends can target users.
/// </summary>
public sealed class AzureNotificationHubDeviceRegistrar(
    INotificationHubClient hubClient,
    ILogger<AzureNotificationHubDeviceRegistrar> logger) : IPushDeviceRegistrar
{
    /// <inheritdoc />
    public async Task<Result> UpsertAsync(UserIdentifierType userId, DeviceInstallationRequest request, CancellationToken cancellationToken = default)
    {
        NotificationPlatform? platform = request.Platform.ToUpperInvariant() switch
        {
            "FCMV1" => NotificationPlatform.FcmV1,
            "APNS" => NotificationPlatform.Apns,
            _ => null,
        };
        if (platform is null)
        {
            return Result.Failure(Error.Validation(
                code: "PushDevice.UnsupportedPlatform",
                message: $"Platform must be '{DeviceInstallationRequest.FcmV1Platform}' or '{DeviceInstallationRequest.ApnsPlatform}'.",
                source: nameof(AzureNotificationHubDeviceRegistrar)));
        }

        var installation = new Installation
        {
            InstallationId = request.InstallationId,
            Platform = platform.Value,
            PushChannel = request.PushChannel,
            Tags = [NativePushPayloads.UserTag(userId)],
        };

        try
        {
            // The id is client-supplied and the upsert is a full overwrite, so writing over another
            // user's installation would re-tag and re-point their device. The installation id is per
            // DEVICE, though, so a second user signing in on the same phone legitimately presents an
            // id the previous user owns: when the request also carries the installation's current
            // push channel, the caller holds the device and the installation is handed over (re-tagged
            // below, so the previous owner's pushes stop). Without the channel the answer is a
            // conflict rather than a silent success: ids are random client GUIDs, so "taken" reveals
            // nothing an attacker could use, and the client recovers by minting a fresh id.
            if (await ClaimedByAnotherDeviceAsync(request, userId, cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure(Error.Conflict(
                    code: "PushDevice.InstallationConflict",
                    message: "The installation id is registered to another device. Generate a new installation id and retry.",
                    source: nameof(AzureNotificationHubDeviceRegistrar)));
            }

            await hubClient.CreateOrUpdateInstallationAsync(installation, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (MessagingException ex)
        {
            logger.LogError(ex, "Device installation upsert failed");
            return Result.Failure(Error.Failure(
                code: "PushDevice.UpsertFailed",
                message: "The device could not be registered for push notifications.",
                source: nameof(AzureNotificationHubDeviceRegistrar)));
        }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(UserIdentifierType userId, string installationId, CancellationToken cancellationToken = default)
    {
        try
        {
            // The hub is the only ownership store: the installation's tags carry the
            // user:{id} stamp written by UpsertAsync, so read-then-delete is the check.
            // A concurrent re-registration of the same id between the two calls is the
            // owner's own doing, so no lock is warranted.
            Installation installation = await hubClient.GetInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
            if (!OwnedBy(installation, userId))
            {
                // Someone else's installation (or one registered before ownership tagging).
                // Reported as success: see the interface remarks on the existence oracle.
                return Result.Success();
            }

            await hubClient.DeleteInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (MessagingEntityNotFoundException)
        {
            // Idempotent delete: an unknown installation is already the desired state.
            return Result.Success();
        }
        catch (MessagingException ex)
        {
            logger.LogError(ex, "Device installation delete failed");
            return DeleteFailed();
        }
    }

    /// <summary>
    /// Whether the installation already exists, carries a different user's owner tag, AND points at a
    /// different push channel than the request. An unknown id, an installation registered before
    /// ownership tagging, or one whose channel the caller presents (the same physical device changing
    /// hands) is free for the caller to claim.
    /// </summary>
    private async Task<bool> ClaimedByAnotherDeviceAsync(DeviceInstallationRequest request, UserIdentifierType userId, CancellationToken cancellationToken)
    {
        Installation existing;
        try
        {
            existing = await hubClient.GetInstallationAsync(request.InstallationId, cancellationToken).ConfigureAwait(false);
        }
        catch (MessagingEntityNotFoundException)
        {
            return false;
        }

        var ownTag = NativePushPayloads.UserTag(userId);
        var ownedByAnotherUser = existing is { Tags: { } tags }
            && tags.Any(tag => tag.StartsWith(NativePushPayloads.UserTagPrefix, StringComparison.Ordinal)
                && !string.Equals(tag, ownTag, StringComparison.Ordinal));
        return ownedByAnotherUser
            && !string.Equals(existing.PushChannel, request.PushChannel, StringComparison.Ordinal);
    }

    private static bool OwnedBy(Installation installation, UserIdentifierType userId) =>
        installation.Tags is { } tags && tags.Contains(NativePushPayloads.UserTag(userId), StringComparer.Ordinal);

    private static Result DeleteFailed() =>
        Result.Failure(Error.Failure(
            code: "PushDevice.DeleteFailed",
            message: "The device could not be unregistered from push notifications.",
            source: nameof(AzureNotificationHubDeviceRegistrar)));
}
