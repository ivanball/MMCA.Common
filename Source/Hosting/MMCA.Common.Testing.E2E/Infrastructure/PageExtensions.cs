using Microsoft.Playwright;

namespace MMCA.Common.Testing.E2E.Infrastructure;

/// <summary>
/// Extension methods for <see cref="IPage"/> to handle Blazor InteractiveAuto rendering.
/// The app uses InteractiveAuto with prerendering — pages appear as static HTML before the
/// WASM runtime initialises. These helpers ensure the page is fully interactive before
/// tests start filling forms or clicking buttons.
/// </summary>
public static class PageExtensions
{
    /// <summary>
    /// Waits for the Blazor runtime to finish initialising after a full page load.
    /// Without this, event handlers are not wired up and clicks/fills are silently ignored.
    /// </summary>
    public static async Task WaitForBlazorAsync(this IPage page, float timeout = 30_000)
    {
        // blazor.web.js sets window.Blazor on load, then populates _internal after the
        // WASM CLR (or SignalR circuit) is ready. The exact properties inside _internal
        // may vary across .NET versions, so we check for any truthy _internal object.
        await page.WaitForFunctionAsync(
            "() => !!window.Blazor?._internal",
            new PageWaitForFunctionOptions { Timeout = timeout });

        // Blazor's component rendering is asynchronous — it happens AFTER the runtime
        // reports as ready. Wait for two animation frames + a short delay to let the
        // render pipeline flush, event handlers get attached, and DOM settle.
        await page.EvaluateAsync(
            "() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(r, 500))))");
    }

    /// <summary>
    /// Navigates to <paramref name="path"/>, waits for network idle, then waits for Blazor
    /// interactivity. Use this instead of the bare GotoAsync + WaitForLoadState pair.
    /// </summary>
    public static async Task GotoAndWaitForBlazorAsync(this IPage page, string path)
    {
        await page.GotoAsync(path);
        // Use Load instead of NetworkIdle — Blazor InteractiveAuto keeps a persistent
        // SignalR WebSocket open, so NetworkIdle is never reached.
        await page.WaitForLoadStateAsync(LoadState.Load);
        await page.WaitForBlazorAsync();
    }

    /// <summary>
    /// Navigates using Blazor's client-side router instead of a full page load.
    /// Use this for auth-protected pages when already logged in — avoids the server-side
    /// prerender which lacks the JWT token stored in browser storage.
    /// Requires Blazor to already be initialised on the current page.
    /// </summary>
    public static async Task BlazorNavigateAsync(this IPage page, string path)
    {
        await page.EvaluateAsync($"() => Blazor.navigateTo('{path}')");
        // Client-side navigation does NOT trigger a new page load event — do NOT wait
        // for LoadState.Load as it will hang. Wait for the URL to change, then let the
        // render pipeline flush so event handlers are attached and components have rendered.
        await page.WaitForFunctionAsync(
            $"() => window.location.pathname === '{path}'",
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await page.EvaluateAsync(
            "() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(r, 500))))");
    }

    /// <summary>
    /// Waits for a full page load + Blazor readiness. Use after clicking a link or button
    /// that triggers a full-page navigation (e.g., clicking "View Details" on a product card).
    /// </summary>
    public static async Task WaitForPageAndBlazorAsync(this IPage page)
    {
        // For full page navigations, wait for the load event. For Blazor enhanced
        // navigation (SPA-style), this resolves immediately — harmless.
        await page.WaitForLoadStateAsync(LoadState.Load);
        // Wait for Blazor render cycle — covers both full-page and enhanced navigation.
        await page.EvaluateAsync(
            "() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(r, 500))))");
    }

    /// <summary>
    /// Fills a form field and verifies the value persisted. Blazor InteractiveAuto can
    /// re-hydrate the page after the runtime loads, replacing pre-rendered inputs and
    /// wiping any values filled before hydration completed. This method retries the fill.
    /// </summary>
    public static async Task FillAndVerifyAsync(this ILocator field, string value, int maxRetries = 5)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            // First try FillAsync (fast, dispatches input+change events)
            await field.FillAsync(value);
            await Task.Delay(300);
            if (await field.InputValueAsync() == value)
                return;

            // Fallback: clear and type character-by-character (fires individual key events
            // that Blazor's event system is more likely to handle after enhanced navigation)
            await field.ClearAsync();
            await field.PressSequentiallyAsync(value, new() { Delay = 20 });
            await Task.Delay(300);
            if (await field.InputValueAsync() == value)
                return;
            await Task.Delay(500);
        }
    }
}
