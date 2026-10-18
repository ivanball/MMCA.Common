using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.Testing.E2E.Workflows.Accessibility;

/// <summary>
/// Keyboard operability gate for an app's own pages (WCAG 2.1 SC 2.1.1 Keyboard, 2.1.2 No Keyboard
/// Trap, 2.4.1 Bypass Blocks, 2.4.3 Focus Order and 2.4.7 Focus Visible), authored once here and
/// re-run as a thin sealed subclass per consumer repo. Four checks, all driven from the keyboard:
/// <list type="number">
/// <item><description>on every page in <see cref="PagePaths"/>, the first Tab stop is a skip link
/// (an anchor whose <c>href</c> names a fragment), and activating it with Enter moves focus into the
/// main landmark;</description></item>
/// <item><description>a dialog opened from <see cref="DialogOpener"/> traps focus: Tab and
/// Shift+Tab never move focus outside it;</description></item>
/// <item><description>Escape closes the dialog and returns focus to the element that opened
/// it;</description></item>
/// <item><description>on every page in <see cref="PagePaths"/>, each of the first Tab stops shows a
/// visible focus indicator: a computed <c>outline-style</c> other than <c>none</c> with a non-zero
/// <c>outline-width</c>, or a <c>box-shadow</c> other than <c>none</c>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The dialog is opened by focusing <see cref="DialogOpener"/> and pressing Enter, not by a mouse
/// click: the gate is about keyboard users, and a click does not focus a button in every engine
/// (WebKit), which would leave nothing for the focus-return check to compare against.
/// <para>
/// The page checks Tab from the START of the document: a page that moves focus on load (Blazor's
/// <c>FocusOnNavigate</c> focuses the first heading) would otherwise start the walk mid-page. The
/// reset focuses a script-only (<c>tabindex="-1"</c>) probe inserted as the first child of
/// <c>body</c>, so the next Tab lands on the first focusable element in document order.
/// </para>
/// </remarks>
public abstract class KeyboardOperabilityTestsBase : E2ETestBase
{
    /// <summary>How many Tab and Shift+Tab presses the focus-trap check makes inside the dialog.</summary>
    private const int TrapTabPresses = 12;

    /// <summary>How many leading Tab stops the focus-indicator check inspects on each page.</summary>
    private const int IndicatorTabStops = 3;

    /// <summary>Wait budget, in milliseconds, for focus to settle after a keyboard action.</summary>
    private const float FocusTimeoutMs = 10_000;

    /// <summary>
    /// Resolves once <c>document.activeElement</c> has stayed the same for 150 ms (or after 3 s at
    /// most), so a read after a keypress sees where focus came to rest rather than a transit stop.
    /// </summary>
    private const string FocusSettledScript =
        "() => new Promise(resolve => { let last = document.activeElement, stable = 0, ticks = 0;"
        + " const timer = setInterval(() => { ticks++;"
        + " if (document.activeElement === last) { stable++; } else { last = document.activeElement; stable = 0; }"
        + " if (stable >= 3 || ticks >= 60) { clearInterval(timer); resolve(); } }, 50); })";

    protected KeyboardOperabilityTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>The pages whose skip link and focus indicators are checked.</summary>
    protected abstract IReadOnlyList<string> PagePaths { get; }

    /// <summary>The page that hosts <see cref="DialogOpener"/>.</summary>
    protected abstract string DialogPagePath { get; }

    /// <summary>
    /// The control on <see cref="DialogPagePath"/> that opens a dialog (an element with
    /// <c>role="dialog"</c>) when it is focused and Enter is pressed. Read through <c>Page</c>.
    /// </summary>
    protected abstract ILocator DialogOpener { get; }

    [Fact]
    public async Task SkipLink_IsFirstTabStop_AndMovesFocusIntoMainLandmark()
    {
        PagePaths.Should().NotBeEmpty("at least one page must be declared, or the skip-link gate verifies nothing");

        foreach (var path in PagePaths)
        {
            await Page.GotoAndWaitForBlazorAsync(path).ConfigureAwait(false);
            await FocusDocumentStartAsync().ConfigureAwait(false);
            await Page.Keyboard.PressAsync("Tab").ConfigureAwait(false);

            var first = await Page.EvaluateAsync<string>(
                "() => { const e = document.activeElement; return e ? e.outerHTML.slice(0, 200) : ''; }").ConfigureAwait(false);
            var isSkipLink = await Page.EvaluateAsync<bool>(
                "() => { const e = document.activeElement; return !!e && e.tagName === 'A' && (e.getAttribute('href') || '').includes('#'); }").ConfigureAwait(false);
            isSkipLink.Should().BeTrue(
                $"the first Tab stop on {path} must be a skip link (an anchor to a fragment), but focus landed on: {first}");

            await Page.Keyboard.PressAsync("Enter").ConfigureAwait(false);

            var inMain = await FocusSettlesAsync(() => Page.EvaluateAsync<bool>(
                "() => !!document.activeElement && !!document.activeElement.closest('main, [role=\"main\"]')")).ConfigureAwait(false);
            inMain.Should().BeTrue($"activating the skip link on {path} must move keyboard focus into the main landmark");
        }
    }

    [Fact]
    public async Task OpenDialog_TrapsTabFocus()
    {
        var dialog = await OpenDialogWithKeyboardAsync().ConfigureAwait(false);

        foreach (var key in Enumerable.Repeat("Tab", TrapTabPresses).Concat(Enumerable.Repeat("Shift+Tab", TrapTabPresses)))
        {
            await Page.Keyboard.PressAsync(key).ConfigureAwait(false);

            // A Blazor focus trap redirects from its edge sentinel through an interop round trip, so
            // the next key must wait until focus stops moving; a trap that fails leaves focus outside
            // for good, which the read below still catches.
            await Page.EvaluateAsync(FocusSettledScript).ConfigureAwait(false);

            var inside = await dialog.EvaluateAsync<bool>("d => d.contains(document.activeElement)").ConfigureAwait(false);
            var focused = await Page.EvaluateAsync<string>(
                "() => { const e = document.activeElement; return e ? e.outerHTML.slice(0, 200) : ''; }").ConfigureAwait(false);
            inside.Should().BeTrue($"pressing {key} inside an open dialog must keep focus inside it (focus trap), but focus moved to: {focused}");
        }
    }

    [Fact]
    public async Task Escape_ClosesDialog_AndReturnsFocusToOpener()
    {
        var dialog = await OpenDialogWithKeyboardAsync().ConfigureAwait(false);

        await Page.Keyboard.PressAsync("Escape").ConfigureAwait(false);

        await Expect(dialog).ToHaveCountAsync(0, new() { Timeout = FocusTimeoutMs }).ConfigureAwait(false);
        await Expect(DialogOpener).ToBeFocusedAsync(new() { Timeout = FocusTimeoutMs }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FocusedElement_ShowsVisibleFocusIndicator()
    {
        PagePaths.Should().NotBeEmpty("at least one page must be declared, or the focus-indicator gate verifies nothing");

        foreach (var path in PagePaths)
        {
            await Page.GotoAndWaitForBlazorAsync(path).ConfigureAwait(false);
            await FocusDocumentStartAsync().ConfigureAwait(false);

            for (var stop = 1; stop <= IndicatorTabStops; stop++)
            {
                await Page.Keyboard.PressAsync("Tab").ConfigureAwait(false);

                var style = await Page.EvaluateAsync<string[]>(
                    "() => { const e = document.activeElement; const s = getComputedStyle(e);"
                    + " return [s.outlineStyle, s.outlineWidth, s.boxShadow, e.outerHTML.slice(0, 200)]; }").ConfigureAwait(false);
                var outlineVisible = style[0] != "none" && style[1] != "0px";
                var boxShadowVisible = style[2] != "none";

                (outlineVisible || boxShadowVisible).Should().BeTrue(
                    string.Create(CultureInfo.InvariantCulture, $"Tab stop {stop} on {path} must show a visible focus indicator, but computed outline-style '{style[0]}', "
                    + $"outline-width '{style[1]}', box-shadow '{style[2]}' on: {style[3]}"));
            }
        }
    }

    /// <summary>
    /// Moves the keyboard's starting point to the top of the document: focuses a script-only probe
    /// inserted as the first child of <c>body</c>, so the next Tab reaches the first focusable element.
    /// </summary>
    /// <returns>The script result, which carries nothing; await it so the probe holds focus.</returns>
    private Task<JsonElement?> FocusDocumentStartAsync() =>
        Page.EvaluateAsync(
            "() => { const p = document.createElement('span'); p.tabIndex = -1;"
            + " p.setAttribute('data-keyboard-start', ''); document.body.prepend(p); p.focus(); }");

    /// <summary>
    /// Loads <see cref="DialogPagePath"/>, focuses <see cref="DialogOpener"/>, presses Enter and waits
    /// for the dialog and for keyboard focus to enter it. Both dialog checks need that precondition:
    /// a trap is only judged once focus is inside, and Escape reaches the dialog's handler only from
    /// within. An opened dialog that leaves focus on its opener fails here.
    /// </summary>
    /// <returns>The open dialog.</returns>
    private async Task<ILocator> OpenDialogWithKeyboardAsync()
    {
        await Page.GotoAndWaitForBlazorAsync(DialogPagePath).ConfigureAwait(false);
        await DialogOpener.FocusAsync().ConfigureAwait(false);
        await Page.Keyboard.PressAsync("Enter").ConfigureAwait(false);

        var dialog = Page.GetByRole(AriaRole.Dialog).First;
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = FocusTimeoutMs }).ConfigureAwait(false);

        var focusEntered = await FocusSettlesAsync(
            () => dialog.EvaluateAsync<bool>("d => d.contains(document.activeElement)")).ConfigureAwait(false);
        focusEntered.Should().BeTrue("opening a dialog must move keyboard focus into it");
        return dialog;
    }

    /// <summary>
    /// Polls a focus predicate until it holds or the wait budget passes. Focus moves asynchronously
    /// after a keyboard action in an interactive Blazor page, so a single read races it.
    /// </summary>
    /// <param name="probe">Reads whether focus is where it should be.</param>
    /// <returns>Whether the predicate held within the budget.</returns>
    private static Task<bool> FocusSettlesAsync(Func<Task<bool>> probe) =>
        E2EPolling.PollUntilAsync(
            probe,
            static held => held,
            TimeSpan.FromMilliseconds(FocusTimeoutMs),
            TimeSpan.FromMilliseconds(100));
}
