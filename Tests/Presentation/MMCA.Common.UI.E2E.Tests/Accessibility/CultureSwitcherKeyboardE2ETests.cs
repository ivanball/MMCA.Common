using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Accessibility;

/// <summary>
/// X-03 (local test runs 18 and 19, WCAG 2.1.1): the culture switcher's control is MudMenu's own
/// activator wrapper (role=button, tabindex 0), which MudBlazor renders <c>display: contents</c>. A
/// display: contents box takes no focus, so forward Tab skipped the Language control and
/// <c>focus()</c> was a no-op. The first app.css rule meant to give it a box lost on specificity to
/// MudBlazor's own <c>.mud-menu&gt;div.mud-menu-activator</c>, and the bUnit test that guarded it read
/// markup only, so it passed. This test reads the computed style and real focus in a browser, with
/// MudBlazor's stylesheet and app.css loaded, in both places the switcher renders: the desktop app bar
/// and the phone top row.
/// </summary>
public sealed class CultureSwitcherKeyboardE2ETests : GalleryAxeTestBase
{
    public CultureSwitcherKeyboardE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Theory]
    [InlineData(1280, 800, ".appbar-icon-actions")]
    [InlineData(390, 844, ".toprow-actions")]
    public async Task LanguageControl_HasABox_TakesFocusByTab_AndEnterOpensTheMenu(int width, int height, string container)
    {
        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync("/");

        // Enter is handled by the interactive circuit, so let it attach before driving the keyboard.
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var selector = $"{container} .culture-switcher > div.mud-menu-activator";
        var activator = Page.Locator(selector).First;
        await Expect(activator).ToBeAttachedAsync();
        await Expect(Page.Locator($"{container} .culture-switcher").First).ToBeVisibleAsync();

        var display = await activator.EvaluateAsync<string>("e => getComputedStyle(e).display");
        Assert.True(
            display != "contents",
            $"X-03: the Language activator computed display '{display}'. A display: contents box takes no focus, "
            + "so the app.css rule giving it a box must out-rank MudBlazor's .mud-menu>div.mud-menu-activator.");

        // focus() reaches it at all.
        await activator.EvaluateAsync("e => e.focus()");
        Assert.True(await IsFocusedAsync(selector), "X-03: focus() on the Language activator left it unfocused.");

        // Forward Tab from the previous focusable element lands on it.
        await Page.Keyboard.PressAsync("Shift+Tab");
        Assert.False(await IsFocusedAsync(selector), "Shift+Tab did not move focus off the Language activator.");
        await Page.Keyboard.PressAsync("Tab");
        Assert.True(
            await IsFocusedAsync(selector),
            "X-03: forward Tab from the previous focusable element skipped the Language activator.");

        // Enter on the focused control opens the language menu.
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Page.Locator(".mud-popover-open .mud-menu-item").First).ToBeVisibleAsync();
    }

    private Task<bool> IsFocusedAsync(string selector) =>
        Page.EvaluateAsync<bool>("s => !!document.activeElement && document.activeElement.matches(s)", selector);
}
