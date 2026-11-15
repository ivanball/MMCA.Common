using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Components.Capabilities;

/// <summary>
/// The JS half of <see cref="BiometricGate"/>'s background lock: the
/// <c>_content/MMCA.Common.UI/app-lock.js</c> module, which sets the <c>inert</c> attribute on every
/// element outside the overlay while the app is locked and removes it on unlock. Every call is best
/// effort: a torn-down circuit or a refused call leaves the focus trap as the only boundary, which is
/// what the gate had before.
/// </summary>
/// <param name="js">The gate's JS runtime.</param>
internal sealed class AppLockInertBackground(IJSRuntime js) : IAsyncDisposable
{
    /// <summary>The module path, as bUnit tests set it up.</summary>
    internal const string ModulePath = "./_content/MMCA.Common.UI/app-lock.js";

    private readonly LazyJsModule _module = new(js, ModulePath);

    /// <summary>Gets a value indicating whether the page behind the overlay was last made inert.</summary>
    public bool IsApplied { get; private set; }

    /// <summary>Makes everything outside <paramref name="lockRoot"/> inert.</summary>
    /// <param name="lockRoot">The element that wraps the overlay.</param>
    /// <returns>A task that completes when the attempt is over, whatever its outcome.</returns>
    public async Task ApplyAsync(ElementReference lockRoot)
    {
        try
        {
            var module = await _module.GetOrImportAsync();
            await module.InvokeVoidAsync("inertOutside", lockRoot);
            IsApplied = true;
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone; nothing to mark.
        }
        catch (JSException)
        {
            // The browser refused the call; the focus trap still holds Tab inside the panel.
        }
    }

    /// <summary>Removes the inert attribute this helper set, if it set any.</summary>
    /// <returns>A task that completes when the attempt is over, whatever its outcome.</returns>
    public async Task ReleaseAsync()
    {
        if (!IsApplied)
        {
            return;
        }

        IsApplied = false;
        try
        {
            var module = await _module.GetOrImportAsync();
            await module.InvokeVoidAsync("release");
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone; the page went with it.
        }
        catch (JSException)
        {
            // Best effort, like the apply.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await ReleaseAsync();
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone; nothing to release.
        }
        catch (JSException)
        {
            // Best effort: ignore shutdown-time interop races.
        }
    }
}
