using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Application.Interfaces.Infrastructure.Notifications;

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

    /// <summary>
    /// Deletes a device installation the given user owns; unknown installation ids and
    /// installations owned by another user both succeed without deleting anything.
    /// </summary>
    /// <param name="userId">The authenticated owner the installation must belong to.</param>
    /// <param name="installationId">The installation to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or a failure result.</returns>
    /// <remarks>
    /// Installation ids are client-generated, but nothing stops a caller from sending someone
    /// else's, so the owning <c>user:{id}</c> tag stamped by <see cref="UpsertAsync"/> is verified
    /// before the delete. Every delete is scoped to an owner: there is no unscoped form, so an
    /// implementation cannot skip the check by accident. A mismatch is reported as success rather
    /// than as a not-found: answering differently for "no such installation" and "not yours" would
    /// turn the endpoint into an existence oracle for other users' installation ids, and the caller
    /// has nothing to do with either answer.
    /// </remarks>
    Task<Result> DeleteAsync(UserIdentifierType userId, string installationId, CancellationToken cancellationToken = default);
}
