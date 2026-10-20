using Microsoft.Azure.NotificationHubs;
using Microsoft.Azure.NotificationHubs.Messaging;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Infrastructure.Notifications.Push;

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
            // The same ownership rule DeleteAsync applies: the id is client-supplied, and the upsert
            // is a full overwrite, so writing over another user's installation would re-tag and
            // re-point their device. Reported as success for the same existence-oracle reason.
            if (await OwnedByAnotherUserAsync(request.InstallationId, userId, cancellationToken).ConfigureAwait(false))
            {
                return Result.Success();
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
    /// Whether the installation already exists and carries a different user's owner tag. An unknown
    /// id, or an installation registered before ownership tagging, is free for the caller to claim.
    /// </summary>
    private async Task<bool> OwnedByAnotherUserAsync(string installationId, UserIdentifierType userId, CancellationToken cancellationToken)
    {
        Installation existing;
        try
        {
            existing = await hubClient.GetInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
        }
        catch (MessagingEntityNotFoundException)
        {
            return false;
        }

        var ownTag = NativePushPayloads.UserTag(userId);
        return existing is { Tags: { } tags }
            && tags.Any(tag => tag.StartsWith(NativePushPayloads.UserTagPrefix, StringComparison.Ordinal)
                && !string.Equals(tag, ownTag, StringComparison.Ordinal));
    }

    private static bool OwnedBy(Installation installation, UserIdentifierType userId) =>
        installation.Tags is { } tags && tags.Contains(NativePushPayloads.UserTag(userId), StringComparer.Ordinal);

    private static Result DeleteFailed() =>
        Result.Failure(Error.Failure(
            code: "PushDevice.DeleteFailed",
            message: "The device could not be unregistered from push notifications.",
            source: nameof(AzureNotificationHubDeviceRegistrar)));
}
