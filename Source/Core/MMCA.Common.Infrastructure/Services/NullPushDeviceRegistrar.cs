using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// No-op device registrar (ADR-044). Accepts and discards registrations so clients can call
/// the Devices endpoints unconditionally; nothing is stored until a downstream host calls
/// <c>AddNativePushNotifications()</c> with an enabled hub configuration.
/// </summary>
public sealed class NullPushDeviceRegistrar : IPushDeviceRegistrar
{
    /// <inheritdoc />
    public Task<Result> UpsertAsync(UserIdentifierType userId, DeviceInstallationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());

    /// <inheritdoc />
    public Task<Result> DeleteAsync(string installationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
