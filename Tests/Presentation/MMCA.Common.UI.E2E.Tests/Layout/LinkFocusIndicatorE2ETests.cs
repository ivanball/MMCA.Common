using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Layout;

/// <summary>
/// X-03 (local test run 9, WCAG 2.4.7): MudBlazor 9.11 ships <c>a:focus-visible{outline:none}</c>, so a
/// MudLink rendered with Underline.Always (the footer legal links, ExternalLink, the auth page links)
/// showed no keyboard focus indicator at all. The login page's "forgot password" MudLink is the same
/// Underline.Always <c>.mud-link</c> as the footer legal links, and the gallery renders it with no
/// backend; it is reached by Tab (so :focus-visible applies) and its computed focus style is read.
/// </summary>
public sealed class LinkFocusIndicatorE2ETests : GalleryAxeTestBase
{
    /// <summary>Upper bound on the tab walk; the login page has far fewer controls than this.</summary>
    private const int MaxTabStops = 40;

    private const string ForgotPasswordLink = "a.mud-link[href='/forgot-password']";

    public LinkFocusIndicatorE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task UnderlinedMudLink_ReachedByKeyboard_ShowsAVisibleFocusIndicator()
    {
        var loginPage = new LoginPage(Page);
        await loginPage.GotoAsync();
        await Expect(Page.Locator(ForgotPasswordLink)).ToBeVisibleAsync();

        var reached = await TabUntilFocusedAsync(ForgotPasswordLink);
        Assert.True(reached, "Tab never reached the login page's forgot-password link.");

        var indicator = await Page.EvaluateAsync<string[]>(
            "() => { const s = getComputedStyle(document.activeElement); return [s.outlineStyle, s.outlineWidth, s.boxShadow]; }");
        var outlineVisible = indicator[0] != "none" && indicator[1] != "0px";
        var boxShadowVisible = indicator[2] != "none";

        Assert.True(
            outlineVisible || boxShadowVisible,
            $"X-03 (WCAG 2.4.7): a keyboard-focused link must show a visible focus indicator, but the focused "
            + $"Underline.Always MudLink computed outline-style '{indicator[0]}', outline-width '{indicator[1]}', "
            + $"box-shadow '{indicator[2]}'. MudBlazor 9.11 ships a:focus-visible with outline: none; app.css must "
            + "restore an outline (or box-shadow) on a:focus-visible and .mud-link:focus-visible.");
    }

    /// <summary>
    /// Tabs forward until the element matching <paramref name="selector"/> holds focus. Bounded so a
    /// regression that makes it unreachable fails with a clear assertion instead of hanging the suite.
    /// </summary>
    private async Task<bool> TabUntilFocusedAsync(string selector)
    {
        for (var i = 0; i < MaxTabStops; i++)
        {
            await Page.Keyboard.PressAsync("Tab");

            var onTarget = await Page.EvaluateAsync<bool>(
                "s => !!document.activeElement && document.activeElement.matches(s)", selector);
            if (onTarget)
            {
                return true;
            }
        }

        return false;
    }
}
