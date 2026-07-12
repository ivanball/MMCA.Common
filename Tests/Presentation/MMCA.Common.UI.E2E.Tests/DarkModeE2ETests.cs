using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests;

/// <summary>
/// Dark-mode axe gate (WCAG 2.1 AA, rubric §20/§21): the same real pages the light-mode gate scans,
/// re-scanned with the dark palette active, so a dark-palette shade whose paired text falls under the
/// AA contrast floor fails the build. Dark mode is activated deterministically by seeding the
/// <c>mmca_theme</c> cookie (the same store <c>theme.js</c> reads), and the scan waits for the theme
/// toggle to report dark mode before running axe.
/// </summary>
public sealed class DarkModeE2ETests : GalleryAxeTestBase
{
    public DarkModeE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task LoginPage_DarkMode_HasNoWcag21AaViolations()
    {
        await SeedDarkThemeAsync();
        await Page.GotoAndWaitForBlazorAsync("/login");
        await WaitForDarkModeAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task ComponentsPage_DarkMode_HasNoWcag21AaViolations()
    {
        await SeedDarkThemeAsync();
        await Page.GotoAndWaitForBlazorAsync("/components");
        await WaitForDarkModeAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    private async Task SeedDarkThemeAsync() =>
        await Page.Context.AddCookiesAsync(
        [
            new Cookie
            {
                Name = "mmca_theme",
                Value = "dark",
                Url = BaseUrl,
            },
        ]);

    // The toggle's accessible name flips once ThemeService has initialized from the cookie and the
    // MudThemeProvider re-rendered with the dark palette — the reliable "dark is applied" signal.
    private async Task WaitForDarkModeAsync() =>
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Switch to light mode" })).ToBeVisibleAsync();
}
