using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services.Navigation;

/// <summary>
/// Result of a hardware-back / WebView-back attempt routed through
/// <see cref="MauiBackNavigationBridge.HandleBackPressedAsync"/>.
/// </summary>
/// <param name="Handled">
/// <see langword="true"/> when the WebView's history stack contained a previous
/// entry and <c>history.back()</c> was invoked. <see langword="false"/> when no
/// previous entry exists.
/// </param>
/// <param name="AtRoot">
/// <see langword="true"/> when the WebView is at the root of its history stack
/// (no previous entry). MAUI hosts typically exit the app on Android when this
/// is reported.
/// </param>
public sealed record BackNavigationResult(bool Handled, bool AtRoot);

/// <summary>
/// Bridges native MAUI back-button events (Android hardware back, iOS swipe gesture)
/// into the BlazorWebView's internal history stack. Call from
/// <c>ContentPage.OnBackButtonPressed</c> via <c>BlazorWebView.TryDispatchAsync</c>
/// so the call runs on the renderer thread with access to the WebView's
/// <see cref="IJSRuntime"/>.
/// </summary>
public static class MauiBackNavigationBridge
{
    private const string ModulePath = "./_content/MMCA.Common.UI/nav-interop.js";

    /// <summary>
    /// Attempts to navigate the WebView one entry back in its history stack via
    /// <c>nav-interop.js</c>'s <c>tryGoBack()</c> helper. Returns a
    /// <see cref="BackNavigationResult"/> describing whether the gesture was
    /// handled and whether the WebView is at the root entry.
    /// </summary>
    public static async ValueTask<BackNavigationResult> HandleBackPressedAsync(IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(js);

        try
        {
            var module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath).ConfigureAwait(false);
            var result = await module.InvokeAsync<BackNavigationResult>("tryGoBack").ConfigureAwait(false);
            return result;
        }
        catch (InvalidOperationException)
        {
            // JS interop unavailable (Blazor not yet hydrated).
            return new BackNavigationResult(Handled: false, AtRoot: true);
        }
        catch (JSDisconnectedException)
        {
            return new BackNavigationResult(Handled: false, AtRoot: true);
        }
        catch (JSException)
        {
            return new BackNavigationResult(Handled: false, AtRoot: true);
        }
    }
}
