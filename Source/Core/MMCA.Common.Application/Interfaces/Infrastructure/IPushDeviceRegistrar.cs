using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Maintains the device-installation registry behind <see cref="INativePushSender"/> (ADR-044).
/// Installations are tagged with the owning user so sends can target users rather than raw
/// tokens. The default implementation is a no-op until a notification hub is configured.
/// </summary>
public interface IPushDeviceRegistrar
{
    /// <summary>Creates or refreshes a device installation, tagging it with the owning user.</summary>
    /// <param name="userId">The authenticated owner of the device.</param>
    /// <param name="request">The installation to upsert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or a validation/failure result.</returns>
    Task<Result> UpsertAsync(UserIdentifierType userId, DeviceInstallationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a device installation; unknown installation ids succeed (idempotent).</summary>
    /// <param name="installationId">The installation to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or a failure result.</returns>
    Task<Result> DeleteAsync(string installationId, CancellationToken cancellationToken = default);
}
