using System.Globalization;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Layout;

/// <summary>
/// The keyboard contract of the mobile hamburger menu, and the first axe scan of the state where it
/// is OPEN (every other scan in the suite runs at 1280px, where the menu does not exist). Two real
/// defects live here: the collapsed menu was hidden with max-height/opacity alone, so its links
/// stayed in the tab order on every phone-width page, and there was no way to close the menu from
/// the keyboard once it was open.
/// </summary>
public sealed class MobileNavKeyboardE2ETests : GalleryAxeTestBase
{
    private const int PhoneWidth = 390;
    private const int PhoneHeight = 844;

    /// <summary>Upper bound on the tab walk; the shell has far fewer controls than this.</summary>
    private const int MaxTabStops = 40;

    public MobileNavKeyboardE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task PhoneViewport_WithTheMenuOpen_HasNoWcag21AaViolations()
    {
        await GoToPhoneHomeAsync();

        await OpenMenuByPointerAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task PhoneViewport_MenuOpensWithEnter_ClosesWithEscape_AndReturnsFocusToTheToggler()
    {
        await GoToPhoneHomeAsync();

        // 1. The hamburger is reachable by Tab at all.
        var reached = await TabUntilTogglerFocusedAsync();
        Assert.True(reached, "Tab never reached the hamburger toggler.");

        var toggler = Page.Locator(".navbar-toggler");
        await Expect(toggler).ToHaveAttributeAsync("aria-expanded", "false");

        // 2. Enter opens it (Space is the checkbox default and works natively; Enter is the one a
        //    role="button" control has to be taught).
        await Page.Keyboard.PressAsync("Enter");
        await Expect(toggler).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(Page.Locator("#nav-menu")).ToBeVisibleAsync();

        // 3. The first nav link is now reachable, and focus actually lands inside the menu.
        await Page.Keyboard.PressAsync("Tab");
        var focusInsideMenu = await Page.EvaluateAsync<bool>(
            "() => !!document.activeElement && !!document.activeElement.closest('#nav-menu')");
        Assert.True(focusInsideMenu, "Tab from the open toggler did not land inside #nav-menu.");

        // 4. Escape closes it and hands focus back to the control that opened it (WCAG 2.1.2/2.4.3).
        await Page.Keyboard.PressAsync("Escape");
        await Expect(toggler).ToHaveAttributeAsync("aria-expanded", "false");

        var focusBackOnToggler = await Page.EvaluateAsync<bool>(
            "() => !!document.activeElement && document.activeElement.classList.contains('navbar-toggler')");
        Assert.True(focusBackOnToggler, "Escape did not return focus to the hamburger toggler.");
    }

    [Fact]
    public async Task PhoneViewport_WithTheMenuClosed_NoNavLinkIsFocusable()
    {
        await GoToPhoneHomeAsync();

        // The collapsed menu must be removed from the accessibility tree, not merely clipped:
        // visibility:hidden is what takes its links out of the tab order.
        var visibility = await Page.EvaluateAsync<string>(
            "() => getComputedStyle(document.querySelector('#nav-menu')).visibility");
        Assert.Equal("hidden", visibility);

        // Belt and braces: walk the whole tab order and prove focus never lands inside the menu.
        for (var i = 0; i < MaxTabStops; i++)
        {
            await Page.Keyboard.PressAsync("Tab");

            var inside = await Page.EvaluateAsync<bool>(
                "() => !!document.activeElement && !!document.activeElement.closest('#nav-menu')");
            Assert.False(
                inside,
                $"A collapsed nav link took focus at tab stop {(i + 1).ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private async Task GoToPhoneHomeAsync()
    {
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight);
        await Page.GotoAndWaitForBlazorAsync("/");

        // Settle before driving or measuring anything: the toggler only behaves once the interactive
        // render has landed.
        await Expect(Page.Locator(".navbar-toggler")).ToBeVisibleAsync();
    }

    /// <summary>Opens the menu the way a pointer user does, then waits for it to actually be shown.</summary>
    private async Task OpenMenuByPointerAsync()
    {
        await Page.Locator(".navbar-toggler").CheckAsync();
        await Expect(Page.Locator("#nav-menu")).ToBeVisibleAsync();
        await Expect(Page.Locator(".navbar-toggler")).ToHaveAttributeAsync("aria-expanded", "true");

        // The menu slides in over 0.2s of opacity. Playwright calls it visible the moment it has a
        // box, and axe blends a partially transparent element against whatever is behind it, so a
        // scan taken mid-animation reports the nav labels at roughly 2.4:1 instead of their real
        // 8.95:1. Wait for the transition to land before measuring anything about colour.
        await Page.WaitForFunctionAsync(
            "() => getComputedStyle(document.querySelector('#nav-menu')).opacity === '1'");
    }

    /// <summary>
    /// Tabs forward until the hamburger holds focus. Bounded rather than unbounded so a regression
    /// that makes it unreachable fails with a clear assertion instead of hanging the suite.
    /// </summary>
    private async Task<bool> TabUntilTogglerFocusedAsync()
    {
        for (var i = 0; i < MaxTabStops; i++)
        {
            await Page.Keyboard.PressAsync("Tab");

            var onToggler = await Page.EvaluateAsync<bool>(
                "() => !!document.activeElement && document.activeElement.classList.contains('navbar-toggler')");
            if (onToggler)
            {
                return true;
            }
        }

        return false;
    }
}
