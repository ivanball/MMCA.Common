namespace MMCA.Common.UI.Services.Capabilities.Media;

/// <summary>
/// Scans a QR code or barcode with the device camera (ADR-042). The head declares the camera
/// permission and the implementation requests it before any camera surface opens.
/// Implementations never throw; a denied permission, a cancelled scan, an unsupported head,
/// or a cancelled token all surface as <see langword="null"/> (or, for
/// <see cref="ScanContinuouslyAsync"/>, as a session that simply ends). The one exception is an
/// exception thrown by the caller's own <c>onScanned</c> callback, which
/// <see cref="ScanContinuouslyAsync"/> hands back after closing the camera surface: it is the
/// caller's failure, not the scanner's, and swallowing it would hide the bug. Web heads keep the
/// null default and hide the scan affordance (<see cref="IsSupported"/> is false) instead of
/// opening a camera surface the browser cannot back - the affordance switch, not a degraded path.
/// The scanned payload is untrusted input: validate it before acting on it.
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

    /// <summary>
    /// Scans code after code until the user or <paramref name="cancellationToken"/> ends the session,
    /// handing each decoded payload to <paramref name="onScanned"/> and waiting for it before the next
    /// scan. A payload equal to the one just handed over, seen again within a few seconds, is not
    /// handed over again, so a code held in view is submitted once (the check-in loop resubmitted it
    /// about once a second, O-24).
    /// </summary>
    /// <param name="onScanned">
    /// Handles one payload and returns the outcome text to show the operator over the camera surface
    /// (for example "Checked in"), or <see langword="null"/> to show nothing. The default calls it on the
    /// caller's own context (so a Blazor component can update its state there), the MAUI implementation on
    /// the main thread. An exception it throws ends the session and is rethrown from this method, except
    /// an <see cref="OperationCanceledException"/> once <paramref name="cancellationToken"/> is cancelled,
    /// which is the session ending.
    /// </param>
    /// <param name="cancellationToken">Ends the session; passed on to <paramref name="onScanned"/>.</param>
    /// <returns>A task that completes when the session ends.</returns>
    /// <remarks>
    /// The default loops <see cref="ScanAsync"/> until it returns <see langword="null"/>, so an
    /// implementation without a continuous surface needs no change. It has nowhere to show the
    /// outcome text, so a caller that needs the outcome seen keeps rendering it itself. The MAUI
    /// implementation keeps one camera page open for the whole session and shows the text there.
    /// </remarks>
    async Task ScanContinuouslyAsync(
        Func<string, CancellationToken, Task<string?>> onScanned,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onScanned);

        // No ConfigureAwait(false): onScanned is the caller's code and runs on the caller's context.
        var repeats = new RepeatScanFilter(TimeProvider.System);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var payload = await ScanAsync(cancellationToken);
                if (payload is null)
                {
                    return;
                }

                if (!repeats.IsRepeat(payload))
                {
                    await onScanned(payload, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The session was cancelled while onScanned ran: that is the session ending, not a fault.
        }
    }
}
