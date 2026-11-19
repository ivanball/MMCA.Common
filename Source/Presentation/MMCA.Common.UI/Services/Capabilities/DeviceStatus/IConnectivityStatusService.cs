namespace MMCA.Common.UI.Services.Capabilities.DeviceStatus;

/// <summary>
/// Reports whether the device currently has network access, so shared components can
/// surface an offline banner and skip doomed API calls. Implementations: MAUI wraps
/// <c>Connectivity.Current</c>; the browser heads (Blazor Server and WebAssembly, through
/// <c>AddBrowserDeviceCapabilities</c>) watch <c>navigator.onLine</c>; a head that registers
/// neither keeps the always-online default (<see cref="AlwaysOnlineConnectivityStatusService"/>).
/// </summary>
public interface IConnectivityStatusService
{
    /// <summary>Raised after <see cref="IsOnline"/> changes. Handlers read the new value from the property.</summary>
    event EventHandler? ConnectivityChanged;

    /// <summary>Whether the device currently has network access. Defaults to <see langword="true"/> until known.</summary>
    bool IsOnline { get; }

    /// <summary>
    /// Starts change monitoring where that requires explicit setup (browser JS listeners).
    /// Call from <c>OnAfterRenderAsync</c>; no-op and safe to call repeatedly on every implementation.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
}
