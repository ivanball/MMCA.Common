namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Client-side orchestration of native push device registration (ADR-044): obtains the platform
/// token from <see cref="IPushDeviceTokenProvider"/> and syncs it to the server's Devices
/// endpoint. Hosts call <see cref="RegisterAsync"/> after sign-in (and on resume) and
/// <see cref="UnregisterAsync"/> BEFORE sign-out clears the tokens (the delete call is
/// authenticated). The default implementation is a no-op on web heads.
/// </summary>
public interface IPushRegistrationService
{
    /// <summary>Whether this head can register for native push at all (native heads only).</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Registers or refreshes this device's installation. Best-effort and safe to call
    /// repeatedly; returns <see langword="false"/> when no platform token is available
    /// (unsupported head, missing credentials, or permission denied) or the sync failed.
    /// </summary>
    Task<bool> RegisterAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes this device's installation. Best-effort; call while still authenticated.</summary>
    Task UnregisterAsync(CancellationToken cancellationToken = default);
}
