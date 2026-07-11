using Microsoft.Azure.NotificationHubs;
using Microsoft.Azure.NotificationHubs.Messaging;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Infrastructure.Services;

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
    public async Task<Result> DeleteAsync(string installationId, CancellationToken cancellationToken = default)
    {
        try
        {
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
            return Result.Failure(Error.Failure(
                code: "PushDevice.DeleteFailed",
                message: "The device could not be unregistered from push notifications.",
                source: nameof(AzureNotificationHubDeviceRegistrar)));
        }
    }
}
