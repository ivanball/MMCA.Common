namespace MMCA.Common.UI.Services.Capabilities.Media;

/// <summary>
/// Scans a QR code or barcode with the device camera (ADR-042). The head declares the camera
/// permission and the implementation requests it before any camera surface opens.
/// Implementations never throw; a denied permission, a cancelled scan, an unsupported head,
/// or a cancelled token all surface as <see langword="null"/>. Web heads keep the null default
/// and hide the scan affordance (<see cref="IsSupported"/> is false) instead of opening a camera
/// surface the browser cannot back - the affordance switch, not a degraded path. The scanned
/// payload is untrusted input: validate it before acting on it.
/// </summary>
public interface IBarcodeScannerService
{
    /// <summary>Whether camera scanning is available on this head.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Requests the camera permission, prompting if the user has not decided yet. Returns
    /// <see langword="false"/> when denied or unavailable, so the caller can explain why scanning
    /// did not start, which a <see langword="null"/> from <see cref="ScanAsync"/> cannot.
    /// </summary>
    Task<bool> EnsureCameraAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the camera scanner and returns the first decoded payload. Returns
    /// <see langword="null"/> when cancelled, denied, or unavailable.
    /// </summary>
    Task<string?> ScanAsync(CancellationToken cancellationToken = default);
}
