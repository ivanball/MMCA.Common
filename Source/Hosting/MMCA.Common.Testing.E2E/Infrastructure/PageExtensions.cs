using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
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
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
[SuppressMessage(
    "Style",
    "IDE0051:Remove unused private members",
    Justification = "The private consts below are consumed only from inside the extension(T) blocks of this class. The IDE0051 analyzer in .NET SDK 10.0.201+ does not see references that cross the boundary between a C# preview extension type block and outer-scope private members of the same containing class, so it reports them as unused. Remove this suppression once Roslyn fixes the cross-block reference tracking.")]
public static class PageExtensions
{
    /// <summary>
    /// The data-row selector of a MudDataGrid rendered in its table (non-card) layout. The default for
    /// the list-page helpers here; a page with its own grid markup passes its own.
    /// </summary>
    public const string MudDataGridRowSelector = ".mud-table-body .mud-table-row";

    /// <summary>
    /// The RUNTIME-LOADED predicate: blazor.web.js sets <c>window.Blazor</c> on load, then populates
    /// <c>_internal</c> once the WASM CLR (or the SignalR circuit) is ready. The exact properties inside
    /// <c>_internal</c> vary across .NET versions, so this checks for any truthy <c>_internal</c> object.
    /// Necessary but NOT sufficient for interactivity, which is why it is only step one of
    /// <c>WaitForBlazorAsync</c>. Shared by the wait and by <c>GotoProtectedAsync</c>'s readiness probe so
    /// the two cannot drift.
    /// </summary>
    private const string BlazorRuntimeLoadedPredicate = "() => !!window.Blazor?._internal";

    /// <summary>
    /// The INTERACTIVE predicate: <c>MmcaThemeProviders</c> (MMCA.Common.UI) stamps
    /// <c>data-mmca-interactive</c> on the document element from its first interactive render, so the
    /// attribute appears only after components have actually attached their event handlers. Keep the
    /// attribute name in step with that component and with <c>theme.js</c>'s <c>markInteractive</c>.
    /// </summary>
    private const string InteractiveMarkerPredicate =
        "() => document.documentElement.hasAttribute('data-mmca-interactive')";

    /// <summary>
    /// A marker-free settle: two animation frames plus a fixed delay. Used by
    /// <c>WaitForPageAndBlazorAsync</c>, which runs after a click that may land on a page with no
    /// interactivity marker at all (a static error page, an external page), so it cannot wait on the
    /// marker and settles on timing alone.
    /// </summary>
    private const string TimedSettleScript =
        "() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(r, 500))))";

    /// <summary>A single animation frame: enough to flush the render pipeline once the marker has confirmed interactivity.</summary>
    private const string SingleFrameSettleScript = "() => new Promise(r => requestAnimationFrame(r))";

    extension(IPage page)
    {
        /// <summary>
        /// Waits for the Blazor runtime to finish initialising after a full page load.
        /// Without this, event handlers are not wired up and clicks/fills are silently ignored.
        /// </summary>
        /// <remarks>
        /// Two phases, because the runtime flag alone is a false green.
        /// <list type="number">
        /// <item><description><b>Runtime loaded.</b> Wait (on the caller's full budget) for
        /// <c>window.Blazor._internal</c>. blazor.web.js sets this once the WASM CLR or the SignalR
        /// circuit reports ready, which is necessary but happens BEFORE components have attached their
        /// handlers, so a click issued here lands on a prerendered-but-dead control and is
        /// swallowed.</description></item>
        /// <item><description><b>Interactive.</b> Wait for the
        /// <c>data-mmca-interactive</c> marker that <c>MmcaThemeProviders</c> (MMCA.Common.UI) stamps
        /// from its first interactive render. That attribute exists only once a real component has
        /// rendered interactively, so it is the honest gate; one animation frame then flushes the render
        /// pipeline.</description></item>
        /// </list>
        /// The marker is the gate, so a page that never stamps it fails this wait rather than
        /// proceeding to click controls that have no handlers attached.
        /// </remarks>
        public async Task WaitForBlazorAsync(float timeout = 30_000)
        {
            await page.WaitForFunctionAsync(
                BlazorRuntimeLoadedPredicate,
                new PageWaitForFunctionOptions { Timeout = timeout }).ConfigureAwait(false);

            await page.WaitForFunctionAsync(
                InteractiveMarkerPredicate,
                new PageWaitForFunctionOptions { Timeout = timeout }).ConfigureAwait(false);

            // Interactivity is confirmed, so only the current render batch still needs to flush.
            await page.EvaluateAsync(SingleFrameSettleScript).ConfigureAwait(false);
        }

        /// <summary>
        /// Navigates to <paramref name="path"/>, waits for network idle, then waits for Blazor
        /// interactivity. Use this instead of the bare GotoAsync + WaitForLoadState pair.
        /// </summary>
        public async Task GotoAndWaitForBlazorAsync(string path)
        {
            await page.GotoAsync(path).ConfigureAwait(false);
            // Use Load instead of NetworkIdle — Blazor InteractiveAuto keeps a persistent
            // SignalR WebSocket open, so NetworkIdle is never reached.
            await page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
            await page.WaitForBlazorAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Navigates using Blazor's client-side router instead of a full page load.
        /// Use this for auth-protected pages when already logged in — avoids the server-side
        /// prerender which lacks the JWT token stored in browser storage.
        /// Requires Blazor to already be initialised on the current page.
        /// </summary>
        public async Task BlazorNavigateAsync(string path)
        {
            // Trigger client-side navigation. A forceLoad navigateTo can tear the JS context down
            // synchronously, so tolerate the evaluate racing with that reload.
            try
            {
                await page.EvaluateAsync($"() => Blazor.navigateTo('{path}')").ConfigureAwait(false);
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
                new PageWaitForFunctionOptions { Timeout = 15_000 }).ConfigureAwait(false);

            // Re-assert Blazor interactivity (fast no-op after a pure SPA nav; waits for re-init after a
            // full reload) and flush the render pipeline. Guard against a context-destroyed race from an
            // in-flight reload, then re-assert on the fresh context.
            try
            {
                await page.WaitForBlazorAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                await page.WaitForBlazorAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Navigates to an auth-protected Blazor page using client-side routing. SSR cannot read JWT
        /// tokens from browser localStorage, so full page loads to [Authorize] pages redirect to /login.
        /// This ensures the Blazor runtime is available (by loading a public page if needed) before using
        /// <see cref="BlazorNavigateAsync"/> for client-side navigation, and re-routes via "/" first so
        /// navigating to the target path always triggers a fresh component lifecycle.
        /// </summary>
        public async Task GotoProtectedAsync(string path)
        {
            ArgumentNullException.ThrowIfNull(page);

            bool blazorReady;
            try
            {
                blazorReady = await page.EvaluateAsync<bool>(BlazorRuntimeLoadedPredicate).ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                blazorReady = false;
            }

            if (!blazorReady)
            {
                // External page (e.g., Stripe) or Blazor not initialized — load a public page first.
                await page.GotoAndWaitForBlazorAsync("/").ConfigureAwait(false);
            }
            else
            {
                // Ensure we're on a different route so navigating to the target path
                // always triggers a fresh component lifecycle (OnInitializedAsync, ServerData, etc.).
                var currentPath = await page.EvaluateAsync<string>("() => window.location.pathname").ConfigureAwait(false);
                if (!string.Equals(currentPath, "/", StringComparison.Ordinal))
                {
                    await page.BlazorNavigateAsync("/").ConfigureAwait(false);
                }
            }

            await page.BlazorNavigateAsync(path).ConfigureAwait(false);
        }

        /// <summary>
        /// Waits for a full page load + Blazor readiness. Use after clicking a link or button
        /// that triggers a full-page navigation (e.g., clicking "View Details" on a product card).
        /// </summary>
        public async Task WaitForPageAndBlazorAsync()
        {
            // For full page navigations, wait for the load event. For Blazor enhanced
            // navigation (SPA-style), this resolves immediately — harmless.
            await page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
            // Wait for Blazor render cycle (covers both full-page and enhanced navigation). This one
            // settles on timing alone: it runs after clicks that may land on a page with no
            // interactivity marker at all, and it never asserts runtime readiness in the first place.
            await page.EvaluateAsync(TimedSettleScript).ConfigureAwait(false);
        }

        /// <summary>
        /// Types <paramref name="text"/> into a list page's search field and waits for the debounced
        /// server filter to surface a matching grid row. The single search helper shared by every
        /// consumer list-page object; it replaces the per-page copies, one of which slept a fixed
        /// 1.5 seconds and hoped.
        /// </summary>
        /// <remarks>
        /// Three things make this reliable where the hand-rolled versions were not.
        /// <list type="number">
        /// <item><description>It waits for the grid's own loading indicator to clear rather than for a
        /// row to exist. Filling the field while the first ServerData page is still arriving loses the
        /// term to the re-render, but preconditioning on a ROW hangs on a list that is legitimately
        /// empty (and on a filtered list whose default filter excludes the fixture's own
        /// data).</description></item>
        /// <item><description>It fills through <see cref="FillAndVerifyAsync"/>, which re-types when a
        /// re-render wipes the value, so a grid that re-renders mid-fill cannot silently swallow the
        /// search term.</description></item>
        /// <item><description>It waits for the EFFECT (the matching row) instead of sleeping a fixed
        /// interval: web-first, so it is both faster on a quick host and correct on a slow one.</description></item>
        /// </list>
        /// </remarks>
        /// <param name="searchField">The list page's search input.</param>
        /// <param name="text">The term to search for, which must also appear in the expected row.</param>
        /// <param name="rowSelector">The grid's data-row selector.</param>
        /// <param name="timeout">Per-step budget in milliseconds.</param>
        public async Task SearchAndWaitForRowAsync(
            ILocator searchField,
            string text,
            string rowSelector = MudDataGridRowSelector,
            float timeout = 15_000)
        {
            ArgumentNullException.ThrowIfNull(page);
            ArgumentNullException.ThrowIfNull(searchField);

            await page.WaitForGridToSettleAsync(timeout).ConfigureAwait(false);
            await searchField.FillAndVerifyAsync(text).ConfigureAwait(false);

            await page.Locator(rowSelector).Filter(new() { HasText = text }).First
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeout }).ConfigureAwait(false);

            await page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
        }

        /// <summary>
        /// Clicks a delete affordance, confirms the dialog it opens, and waits for the resulting grid
        /// reload to settle. Targets the confirmation button by TEST ID rather than by a MudBlazor
        /// class: <c>.mud-message-box .mud-button-filled</c> matches whichever filled button the
        /// message box renders first, which silently changed meaning when a dialog gained a second
        /// filled action.
        /// </summary>
        /// <remarks>
        /// The settle at the end is the part that is easy to leave out and expensive to debug: a
        /// MudDataGrid reloads its page asynchronously after a delete, and an assertion issued against
        /// the pre-reload DOM still sees the deleted row.
        /// </remarks>
        /// <param name="deleteButton">The row's (or detail page's) delete button.</param>
        /// <param name="confirmTestId">The <c>data-testid</c> of the confirm button inside the dialog.</param>
        /// <param name="timeout">Per-step budget in milliseconds.</param>
        public async Task ConfirmDeleteAsync(
            ILocator deleteButton,
            string confirmTestId = "confirm-delete",
            float timeout = 10_000)
        {
            ArgumentNullException.ThrowIfNull(page);
            ArgumentNullException.ThrowIfNull(deleteButton);

            await deleteButton
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeout }).ConfigureAwait(false);
            await deleteButton.ClickAsync().ConfigureAwait(false);

            var confirmButton = page.GetByTestId(confirmTestId);
            await confirmButton
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeout }).ConfigureAwait(false);
            await confirmButton.ClickAsync().ConfigureAwait(false);

            await page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
            await page.WaitForGridToSettleAsync(timeout).ConfigureAwait(false);
        }

        /// <summary>
        /// Waits for a MudDataGrid page's async load to finish, WITHOUT requiring the grid to have
        /// produced any rows. Deliberately not a wait for a row: an empty result set is a legitimate
        /// state, and a helper that preconditions on rows turns it into a timeout.
        /// </summary>
        /// <param name="timeout">Budget in milliseconds.</param>
        public async Task WaitForGridToSettleAsync(float timeout = 15_000)
        {
            ArgumentNullException.ThrowIfNull(page);

            await Assertions.Expect(page.Locator("[role='progressbar']"))
                .ToHaveCountAsync(0, new() { Timeout = timeout }).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs an axe-core accessibility scan against the current page and throws an
        /// <see cref="AccessibilityViolationException"/> if any violation is found. Call from a
        /// consumer E2E test once the page is interactive (e.g. after
        /// <see cref="GotoAndWaitForBlazorAsync"/>).
        /// </summary>
        public async Task AssertNoAccessibilityViolationsAsync(AxeRunOptions? options = null)
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
    }

    extension(ILocator locator)
    {
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
        public async Task FillAndVerifyAsync(string value, float timeout = 10_000)
        {
            ArgumentNullException.ThrowIfNull(locator);

            // FillAsync is fast and dispatches input+change events in one shot.
            await locator.FillAsync(value).ConfigureAwait(false);
            try
            {
                await Assertions.Expect(locator).ToHaveValueAsync(value, new() { Timeout = timeout }).ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // The pre-render value was wiped by Blazor re-hydration before it stuck. Re-type
                // character-by-character (individual key events the Blazor event system reliably
                // handles after enhanced navigation), then assert it persists.
                await locator.ClearAsync().ConfigureAwait(false);
                await locator.PressSequentiallyAsync(value, new() { Delay = 20 }).ConfigureAwait(false);
                await Assertions.Expect(locator).ToHaveValueAsync(value, new() { Timeout = timeout }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Clicks this locator (a button) and waits for <paramref name="expected"/> (the action's visible
        /// effect, e.g. a "saved successfully" snackbar) to appear, re-clicking if it does not. This is the
        /// submit-side counterpart to <see cref="FillAndVerifyAsync"/>: Blazor InteractiveAuto prerenders the
        /// page as static HTML, so a click that lands before the runtime wires the component's
        /// <c>@onclick</c> is silently ignored (see the type-level remarks) and the action never fires. On a
        /// fast host the bare click routinely beats hydration, so a single click is unreliable. This polls:
        /// click, wait a slice of <paramref name="timeout"/> for the effect, and if it has not appeared
        /// re-assert interactivity and click again. A successful action surfaces its effect well within one
        /// slice, so a genuinely-applied click is not re-issued (no double submit); only a no-op click is
        /// retried. Use for create/edit/delete submits that assert on a resulting snackbar or navigation.
        /// </summary>
        public async Task ClickAndVerifyAsync(ILocator expected, float timeout = 15_000)
        {
            ArgumentNullException.ThrowIfNull(locator);
            ArgumentNullException.ThrowIfNull(expected);

            var page = locator.Page;
            // The handler must be wired before the first click counts; without this the click is a no-op.
            await page.WaitForBlazorAsync().ConfigureAwait(false);

            const int maxAttempts = 3;
            var perAttempt = timeout / maxAttempts;
            for (var attempt = 1; attempt < maxAttempts; attempt++)
            {
                await locator.ClickAsync().ConfigureAwait(false);
                try
                {
                    await Assertions.Expect(expected).ToBeVisibleAsync(new() { Timeout = perAttempt }).ConfigureAwait(false);
                    return;
                }
                catch (PlaywrightException)
                {
                    // The click most likely did not register (handler not yet attached on this fast host).
                    // Re-assert interactivity, then click again on the next loop turn.
                    await page.WaitForBlazorAsync().ConfigureAwait(false);
                }
            }

            // Final attempt: click once more and let the assertion throw with full diagnostics if it still
            // has not taken effect (a real failure, not a hydration race).
            await locator.ClickAsync().ConfigureAwait(false);
            await Assertions.Expect(expected).ToBeVisibleAsync(new() { Timeout = perAttempt }).ConfigureAwait(false);
        }

        /// <summary>
        /// Clicks a locator that navigates (typically an in-cell link on a grid row) and verifies the
        /// navigation actually happened, re-clicking if it did not. This is the navigation-side
        /// counterpart to <see cref="ClickAndVerifyAsync"/>: list pages often have NO RowClick handler
        /// because every cell wraps its content in a MudLink Href, so clicking the ROW element's center
        /// lands on cell padding between the inline anchors and silently does nothing. Call this on
        /// <c>row.GetByRole(AriaRole.Link).First</c>, never on the row itself; the URL verify stays as
        /// the belt because a link click can still race a grid re-render. Waits for the page and Blazor
        /// to settle once the URL matches <paramref name="urlPattern"/> (a regular expression).
        /// </summary>
        public async Task ClickAndWaitForUrlAsync(IPage page, string urlPattern)
        {
            ArgumentNullException.ThrowIfNull(locator);
            ArgumentNullException.ThrowIfNull(page);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                await locator.ClickAsync().ConfigureAwait(false);
                try
                {
                    await page.WaitForURLAsync(new Regex(urlPattern), new() { Timeout = 5_000 }).ConfigureAwait(false);
                    await page.WaitForPageAndBlazorAsync().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
                {
                    // Swallowed click: the URL never changed. Re-click the now-settled element.
                }
            }

            await locator.ClickAsync().ConfigureAwait(false);
            await page.WaitForURLAsync(new Regex(urlPattern), new() { Timeout = 15_000 }).ConfigureAwait(false);
            await page.WaitForPageAndBlazorAsync().ConfigureAwait(false);
        }
    }

    // Collapses a violating node's markup to a single trimmed line so the failure message points at the
    // exact offending element (e.g. which low-contrast text node) without dumping multi-line HTML.
    [SuppressMessage(
        "Style",
        "IDE0051:Remove unused private members",
        Justification = "Called from AssertNoAccessibilityViolationsAsync inside the extension(IPage page) block above. The IDE0051 analyzer in .NET SDK 10.0.201+ does not see references that cross the boundary between a C# preview extension type block and outer-scope private members of the same containing class, so it reports a false positive. The local SDK 10.0.104 analyzer correctly resolves the call. Remove this suppression once Roslyn fixes the cross-block reference tracking.")]
    private static string CompactHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "(no markup)";
        }

        var collapsed = string.Join(' ', html.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 220 ? string.Concat(collapsed.AsSpan(0, 220), "…") : collapsed;
    }
}
