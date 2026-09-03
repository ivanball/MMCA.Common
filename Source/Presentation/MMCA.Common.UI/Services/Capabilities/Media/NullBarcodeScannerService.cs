namespace MMCA.Common.UI.Services.Capabilities.Media;

/// <summary>
/// No-op barcode scanner (ADR-042). Browsers have no shared camera-scanning primitive the
/// framework can rely on, so web heads keep this default and simply hide the scan button
/// (<see cref="IsSupported"/> is false); the native override ships in
/// <c>MMCA.Common.UI.Maui</c> and is opt-in.
/// </summary>
public sealed class NullBarcodeScannerService : IBarcodeScannerService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<string?> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
