using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Infrastructure.Notifications.Push;

/// <summary>
/// No-op device registrar (ADR-044). Accepts and discards registrations so clients can call
/// the Devices endpoints unconditionally; nothing is stored until a downstream host calls
/// a push adapter (<c>AddAzureNotificationHubsPush(configuration)</c> from <c>MMCA.Common.Azure</c>,
/// <c>AddAmazonSnsPush(configuration)</c> from <c>MMCA.Common.Aws</c>, or
/// <c>AddFirebaseCloudMessagingPush(configuration)</c> from <c>MMCA.Common.Gcp</c>) with an enabled configuration.
/// </summary>
public sealed class NullPushDeviceRegistrar : IPushDeviceRegistrar
{
    /// <inheritdoc />
    public Task<Result> UpsertAsync(UserIdentifierType userId, DeviceInstallationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());

    /// <inheritdoc />
    public Task<Result> DeleteAsync(UserIdentifierType userId, string installationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
