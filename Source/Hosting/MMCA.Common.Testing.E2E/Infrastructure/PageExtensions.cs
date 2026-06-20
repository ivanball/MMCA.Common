using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
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
    /// Ensures the current page is interactive under the <b>WebAssembly</b> runtime, not the Blazor
    /// <b>Server</b> circuit. The app uses InteractiveAuto + prerender: on a cold load the page activates
    /// the Server circuit while WASM downloads in the background, so the component's event handlers — and
    /// the auth POST they issue (<c>auth/login</c> / <c>auth/register</c>) — run server-side, i.e.
    /// UI-host-circuit → gateway → Identity. That contended <i>double hop</i> is what times out / 500s
    /// ("Registration failed: One or more errors occurred.") on the 2-core CI runner. Once WASM has
    /// downloaded, a full reload re-activates the page under WASM so the POST is a single
    /// browser → gateway → Identity hop.
    /// <para>
    /// Best-effort and fail-safe: if WASM is already active it returns immediately (no reload); if WASM
    /// doesn't boot within <paramref name="wasmReadyTimeout"/> (or any probe/reload error occurs) it
    /// returns without reloading and the caller proceeds in Server mode — never worse than not calling
    /// it. Pass <c>0</c> (or set <c>E2E_WASM_READY_TIMEOUT=0</c>) to skip the wait entirely.
    /// </para>
    /// </summary>
    public static async Task EnsureWasmInteractiveAsync(this IPage page, float? wasmReadyTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var timeout = wasmReadyTimeout ?? E2ETestConfiguration.WasmReadyTimeout;
        if (timeout <= 0)
        {
            return;
        }

        // `getDotnetRuntime` is the global the WASM loader (dotnet.js) defines once the runtime module is
        // imported; `getDotnetRuntime(0)` returns the booted runtime instance. It is undefined under a
        // pure Server-circuit render (WASM not downloaded), so it is a reliable "WASM has booted" probe.
        const string wasmReadyProbe =
            "() => { try { return typeof window.getDotnetRuntime === 'function' && !!window.getDotnetRuntime(0); } catch { return false; } }";

        try
        {
            // Fast path: already WASM-interactive (e.g. a warm browser context) — nothing to do.
            if (await page.EvaluateAsync<bool>(wasmReadyProbe))
            {
                return;
            }

            // Wait for WASM to finish downloading + booting in the background, then reload so the page
            // (activated Server-first) re-renders under the now-cached WASM runtime.
            await page.WaitForFunctionAsync(wasmReadyProbe, new PageWaitForFunctionOptions { Timeout = timeout });
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load });
            await page.WaitForBlazorAsync();
        }
        catch (PlaywrightException)
        {
            // Timeout on a contended runner, or a reload/eval raced with a navigation — fall back to the
            // Server-mode submit (unchanged behaviour). PlaywrightException covers TimeoutException too.
        }
    }

    /// <summary>
    /// Navigates using Blazor's client-side router instead of a full page load.
    /// Use this for auth-protected pages when already logged in — avoids the server-side
    /// prerender which lacks the JWT token stored in browser storage.
    /// Requires Blazor to already be initialised on the current page.
    /// </summary>
    public static async Task BlazorNavigateAsync(this IPage page, string path)
    {
        // Trigger client-side navigation. A forceLoad navigateTo can tear the JS context down
        // synchronously, so tolerate the evaluate racing with that reload.
        try
        {
            await page.EvaluateAsync($"() => Blazor.navigateTo('{path}')");
        }
        catch (PlaywrightException)
        {
            // navigateTo did an immediate full reload that destroyed the context mid-call — fine,
            // the navigation is already underway.
        }

        // Client-side navigation fires NO load event, so do NOT use WaitForURLAsync (its default
        // WaitUntil=Load hangs on same-document nav and leaves the page perpetually "navigating",
        // blocking later actions). Poll window.location instead — Playwright re-injects this across
        // a full reload too, so it settles on the target pathname either way.
        await page.WaitForFunctionAsync(
            $"() => window.location.pathname === '{path}'",
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        // Re-assert Blazor interactivity (fast no-op after a pure SPA nav; waits for re-init after a
        // full reload) and flush the render pipeline. Guard against a context-destroyed race from an
        // in-flight reload, then re-assert on the fresh context.
        try
        {
            await page.WaitForBlazorAsync();
        }
        catch (PlaywrightException)
        {
            await page.WaitForBlazorAsync();
        }
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
    /// Fills a form field and waits for the value to persist. Blazor InteractiveAuto re-hydrates the
    /// page after the runtime loads, replacing pre-rendered inputs and wiping any value filled before
    /// hydration completed. Rather than sleeping a fixed interval and hoping hydration has finished,
    /// this fills the field then uses Playwright's auto-waiting <c>ToHaveValueAsync</c> assertion,
    /// which polls until the value sticks (or <paramref name="timeout"/> elapses). If the pre-render
    /// value was wiped before it stuck, the field is re-typed character-by-character and re-asserted.
    /// This is the single fill helper shared by <c>E2ETestBase</c>, <c>LoginPage</c>, and
    /// <c>RegisterPage</c>; it replaces the duplicated fixed-delay retry loops.
    /// </summary>
    public static async Task FillAndVerifyAsync(this ILocator field, string value, float timeout = 10_000)
    {
        ArgumentNullException.ThrowIfNull(field);

        // FillAsync is fast and dispatches input+change events in one shot.
        await field.FillAsync(value);
        try
        {
            await Assertions.Expect(field).ToHaveValueAsync(value, new() { Timeout = timeout });
        }
        catch (PlaywrightException)
        {
            // The pre-render value was wiped by Blazor re-hydration before it stuck. Re-type
            // character-by-character (individual key events the Blazor event system reliably
            // handles after enhanced navigation), then assert it persists.
            await field.ClearAsync();
            await field.PressSequentiallyAsync(value, new() { Delay = 20 });
            await Assertions.Expect(field).ToHaveValueAsync(value, new() { Timeout = timeout });
        }
    }

    /// <summary>
    /// Runs an axe-core accessibility scan against the current page and throws an
    /// <see cref="AccessibilityViolationException"/> if any violation is found. Call from a
    /// consumer E2E test once the page is interactive (e.g. after
    /// <see cref="GotoAndWaitForBlazorAsync"/>).
    /// </summary>
    public static async Task AssertNoAccessibilityViolationsAsync(this IPage page, AxeRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var result = options is null
            ? await page.RunAxe().ConfigureAwait(false)
            : await page.RunAxe(options).ConfigureAwait(false);

        if (result.Violations.Length == 0)
        {
            return;
        }

        var summary = string.Join(
            Environment.NewLine,
            result.Violations.Select(v =>
            {
                var nodes = string.Join(
                    Environment.NewLine,
                    v.Nodes.Select(n => $"      → {CompactHtml(n.Html)}"));
                return $"  [{v.Impact}] {v.Id}: {v.Help} ({v.Nodes.Length} node(s)){Environment.NewLine}{nodes}";
            }));

        throw new AccessibilityViolationException(
            $"{result.Violations.Length} accessibility violation(s) found:{Environment.NewLine}{summary}");
    }

    // Collapses a violating node's markup to a single trimmed line so the failure message points at the
    // exact offending element (e.g. which low-contrast text node) without dumping multi-line HTML.
    private static string CompactHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "(no markup)";
        }

        var collapsed = string.Join(' ', html.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 220 ? collapsed[..220] + "…" : collapsed;
    }
}
