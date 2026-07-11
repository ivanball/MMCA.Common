using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services.Capabilities.Browser;

/// <summary>
/// Browser <see cref="IConnectivityStatusService"/> over <c>navigator.onLine</c> plus the
/// window <c>online</c>/<c>offline</c> events. Reports online until
/// <see cref="InitializeAsync"/> runs (call it from <c>OnAfterRenderAsync</c>; it is a
/// prerender-safe no-op before hydration).
/// </summary>
public sealed class BrowserConnectivityStatusService : IConnectivityStatusService, IAsyncDisposable
{
    private readonly CapabilitiesJsModule _module;
    private DotNetObjectReference<BrowserConnectivityStatusService>? _selfReference;
    private bool _watching;

    /// <summary>Initializes the service over the shared capabilities JS module.</summary>
    public BrowserConnectivityStatusService(CapabilitiesJsModule module) => _module = module;

    /// <inheritdoc />
    public event EventHandler? ConnectivityChanged;

    /// <inheritdoc />
    public bool IsOnline { get; private set; } = true;

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_watching)
        {
            return;
        }

        _selfReference ??= DotNetObjectReference.Create(this);
        var online = await _module
            .InvokeOrDefaultAsync<bool?>("watchOnline", [_selfReference], cancellationToken)
            .ConfigureAwait(false);

        if (online is null)
        {
            // JS unavailable (prerender) — retry on a later call.
            return;
        }

        _watching = true;
        UpdateStatus(online.Value);
    }

    /// <summary>JS callback target for the window <c>online</c>/<c>offline</c> listeners. Not for app code.</summary>
    [JSInvokable]
    public void OnBrowserConnectivityChanged(bool isOnline) => UpdateStatus(isOnline);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_watching)
        {
            await _module.InvokeOrDefaultAsync<bool?>("unwatchOnline", [], CancellationToken.None).ConfigureAwait(false);
        }

        _selfReference?.Dispose();
        _selfReference = null;
    }

    private void UpdateStatus(bool isOnline)
    {
        if (IsOnline == isOnline)
        {
            return;
        }

        IsOnline = isOnline;
        ConnectivityChanged?.Invoke(this, EventArgs.Empty);
    }
}
