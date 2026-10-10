using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Layout;

/// <summary>
/// X-03 (local test run 10, WCAG 2.4.7): MudBlazor 9.11 strips the outline from <c>.mud-button-root</c>
/// and gives a keyboard-focused button only a faint hover tint. MMCA.Common restored a ring for the
/// desktop app bar only (scoped to <c>.appbar-container</c>), so an in-content MudButton such as the
/// login page's "Sign in" submit button shows no focus indicator. The gallery renders that page with no
/// backend; the button is reached by Tab (so :focus-visible applies) and its computed outline is read.
/// Box-shadow is deliberately NOT accepted as the indicator here: a filled MudButton already carries an
/// elevation box-shadow when unfocused, so a non-none box-shadow proves nothing about focus.
/// </summary>
public sealed class ButtonFocusIndicatorE2ETests : GalleryAxeTestBase
{
    /// <summary>Upper bound on the tab walk; the login page has far fewer controls than this.</summary>
    private const int MaxTabStops = 40;

    private const string SignInButton = "button.mud-button-root[type='submit']";

    public ButtonFocusIndicatorE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task InContentMudButton_ReachedByKeyboard_ShowsAVisibleFocusOutline()
    {
        var loginPage = new LoginPage(Page);
        await loginPage.GotoAsync();
        await Expect(Page.Locator(SignInButton)).ToBeVisibleAsync();

        var reached = await TabUntilFocusedAsync(SignInButton);
        Assert.True(reached, "Tab never reached the login page's Sign in button.");

        var indicator = await Page.EvaluateAsync<string[]>(
            "() => { const s = getComputedStyle(document.activeElement); return [s.outlineStyle, s.outlineWidth]; }");
        var outlineVisible = indicator[0] != "none" && indicator[1] != "0px";

        Assert.True(
            outlineVisible,
            $"X-03 (WCAG 2.4.7): a keyboard-focused in-content MudButton must show a visible focus outline, but the "
            + $"focused Sign in button computed outline-style '{indicator[0]}', outline-width '{indicator[1]}'. "
            + "MudBlazor 9.11 strips the outline from .mud-button-root and the only restored ring is scoped to "
            + ".appbar-container; app.css must restore an outline on .mud-button-root:focus-visible.");
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
