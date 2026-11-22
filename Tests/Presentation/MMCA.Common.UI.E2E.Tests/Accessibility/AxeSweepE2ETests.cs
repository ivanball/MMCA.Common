using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.Accessibility;

/// <summary>
/// WCAG 2.1 AA axe gate for the anonymous gallery pages whose scan is nothing more than "open the
/// route, run axe": the four auth forms in their initial state and the components showcase. A scan
/// that signs in, seeds data, switches theme or interacts first stays in its page's own test class
/// (the Forgot Password confirmation state, the grid, the notification, role and session pages, and
/// the dark-mode gate).
/// </summary>
public sealed class AxeSweepE2ETests : GalleryAxeTestBase
{
    public AxeSweepE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    // /components also covers the loading (aria-busy / named progressbar) and error (alert)
    // primitive states added to the showcase. Adding the loading state surfaced and fixed a real
    // defect: PageLoadingState carried a prohibited aria-label on a bare div and an anonymous
    // progressbar. (Dark mode is scanned separately by DarkModeE2ETests.)
    [Theory]
    [InlineData("/login")]
    [InlineData("/register")]
    [InlineData("/reset-password")]
    [InlineData("/forgot-password")]
    [InlineData("/components")]
    [InlineData("/login?reason=two-factor-enabled")]
    [InlineData("/login?reason=two-factor-required")]
    public async Task Page_HasNoWcag21AaViolations(string route)
    {
        await Page.GotoAndWaitForBlazorAsync(route);

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    // v1.314.0: the signed-in two-factor account page in each state the gallery opens by URL (off,
    // required, on, on with the sign-in-again banner, load failed). The states reached by clicks (set up,
    // recovery codes, a refused code, the turn-off dialog) and the new sign-in and register states are
    // scanned in TwoFactorPageE2ETests, LoginSecondFactorE2ETests and RegisterPendingE2ETests.
    [Theory]
    [InlineData("/profile/two-factor", "two-factor-begin")]
    [InlineData("/profile/two-factor?required=true", "two-factor-required")]
    [InlineData("/profile/two-factor?gallery-state=on", "two-factor-codes-remaining")]
    [InlineData("/profile/two-factor?gallery-state=on&required=true", "two-factor-required-sign-in-again")]
    [InlineData("/profile/two-factor?gallery-state=fail", "two-factor-retry")]
    public async Task SignedInPage_HasNoWcag21AaViolations(string route, string stateTestId)
    {
        await Page.Context.AddCookiesAsync(
        [
            new Microsoft.Playwright.Cookie
            {
                Name = "gallery_auth",
                Value = "1",
                Url = BaseUrl,
            },
        ]);
        await Page.GotoAndWaitForBlazorAsync(route);
        await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId(stateTestId)).ToBeVisibleAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }
}
