using Microsoft.JSInterop;
using MMCA.Common.UI.Common.Interfaces;

namespace MMCA.Common.UI.Services;

/// <summary>
/// <see cref="IPendingToastService"/> over <c>sessionStorage</c>, through the same
/// <c>nav-interop.js</c> module (<c>sessionSet</c> / <c>sessionGet</c> / <c>sessionRemove</c>) that
/// <see cref="ListPageStateService"/> uses. One fixed key holds the single pending toast as
/// <c>{ severity, message }</c>. Every interop failure is swallowed: during prerender there is no
/// JS runtime, a circuit can be torn down mid-call, and the browser can refuse storage outright.
/// </summary>
internal sealed class PendingToastService(IJSRuntime js) : IPendingToastService, IAsyncDisposable
{
    /// <summary>The <c>sessionStorage</c> key the pending toast is parked under.</summary>
    internal const string StorageKey = "mmca.pendingToast";

    private const string ModulePath = "./_content/MMCA.Common.UI/nav-interop.js";

    private readonly LazyJsModule _module = new(js, ModulePath);

    /// <inheritdoc />
    public async ValueTask QueueAsync(ToastSeverity severity, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            var module = await _module.GetOrImportAsync(ct).ConfigureAwait(false);
            await module.InvokeVoidAsync("sessionSet", ct, StorageKey, new PendingToast(severity, message)).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            // The toast is a courtesy; losing it must not fail the action that raised it.
        }
    }

    /// <inheritdoc />
    public async ValueTask<(ToastSeverity Severity, string Message)?> TakeAsync(CancellationToken ct = default)
    {
        try
        {
            var module = await _module.GetOrImportAsync(ct).ConfigureAwait(false);
            var pending = await module.InvokeAsync<PendingToast?>("sessionGet", ct, StorageKey).ConfigureAwait(false);
            if (pending is null)
            {
                return null;
            }

            // Remove before showing, so a second render (or a second tab restoring this session)
            // cannot show the same toast twice.
            await module.InvokeVoidAsync("sessionRemove", ct, StorageKey).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(pending.Message) || !Enum.IsDefined(pending.Severity)
                ? null
                : (pending.Severity, pending.Message);
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _module.DisposeAsync();

    // Prerender (InvalidOperationException), a torn-down circuit (JSDisconnectedException), a
    // browser-side failure (JSException) and an interop timeout or cancellation (TaskCanceledException).
    private static bool IsInteropUnavailable(Exception ex) =>
        ex is InvalidOperationException or JSDisconnectedException or JSException or TaskCanceledException;

    /// <summary>The stored payload; serialized camelCase by JS interop as <c>{ severity, message }</c>.</summary>
    internal sealed record PendingToast(ToastSeverity Severity, string Message);
}
