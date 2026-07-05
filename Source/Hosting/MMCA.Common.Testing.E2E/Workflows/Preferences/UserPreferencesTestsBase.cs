using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.Testing.E2E.Workflows.Preferences;

/// <summary>
/// Culture-switch + theme-toggle workflow fitness tests (ADR-027/028), authored once and re-run as a
/// thin subclass in each consumer repo. Fully self-contained: the probe page is the shared /login
/// (Common UI owns it in every app), the probe string is Auth.Login.WelcomeBack ("Welcome Back" /
/// "Bienvenido de nuevo"), and persistence is the anonymous cookie pair (.AspNetCore.Culture +
/// mmca_theme), so no app-specific overrides are needed. The mobile fact pins the v1.103.0
/// regression (controls lived only in the app bar, which is hidden below 1024px).
/// </summary>
public abstract class UserPreferencesTestsBase : E2ETestBase
{
    protected UserPreferencesTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    // Both a desktop app-bar instance and a mobile top-row instance are ALWAYS in the DOM (one
    // hidden by the 1024px media query), so every selector must filter to the visible one.
    private ILocator VisibleCultureButton => Page.Locator("button[aria-label='Language']:visible");

    private ILocator VisibleThemeToggle => Page.Locator("button[aria-label='Switch to dark mode']:visible");

    [Fact]
    public async Task CultureSwitch_ToSpanish_ShouldLocalizeAndPersist()
    {
        // Arrange — anonymous /login renders the English probe.
        await Page.GotoAndWaitForBlazorAsync("/login");
        await Expect(Page.GetByText("Welcome Back")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // Act — pick Español. The switcher forceLoad-navigates through GET /culture/set (which
        // writes the .AspNetCore.Culture cookie) and LocalRedirects back to /login.
        await VisibleCultureButton.ClickAsync();
        await Page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Español" }).ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);

        // Assert — the probe is Spanish, and stays Spanish across a fresh full page load (cookie
        // persistence, not just the in-flight request).
        await Expect(Page.GetByText("Bienvenido de nuevo")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load });
        await Expect(Page.GetByText("Bienvenido de nuevo")).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }

    [Fact]
    public async Task ThemeToggle_ToDark_ShouldApplyAndPersist()
    {
        await Page.GotoAndWaitForBlazorAsync("/login");

        // Act — toggle dark. No navigation: MudThemeProvider re-emits its CSS variables.
        await VisibleThemeToggle.ClickAsync();

        // Assert — the palette background variable flips to the dark value (MMCATheme PaletteDark),
        // and the toggle's accessible name flips. Then reload: theme.js + ThemeService re-apply the
        // persisted mmca_theme cookie/localStorage value on the fresh document.
        await AssertDarkPaletteAsync();
        await Expect(Page.Locator("button[aria-label='Switch to light mode']:visible"))
            .ToBeVisibleAsync(new() { Timeout = 15_000 });

        await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load });
        await AssertDarkPaletteAsync();
    }

    [Fact]
    public async Task MobileViewport_CultureAndTheme_ShouldBeReachable()
    {
        // Pin the v1.103.0 mobile-parity fix in the real app: below 1024px the app bar is hidden
        // and the controls must come from NavMenu's top row instead.
        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAndWaitForBlazorAsync("/login");

        await Expect(Page.Locator(".toprow-actions button[aria-label='Language']"))
            .ToBeVisibleAsync(new() { Timeout = 15_000 });

        // The mobile theme toggle must actually work, not merely render.
        await Page.Locator(".toprow-actions button[aria-label='Switch to dark mode']").ClickAsync();
        await AssertDarkPaletteAsync();
    }

    // The dark palette's background token (#1A2027) read off the emitted CSS variables: independent
    // of which element happens to paint it, and polls until the provider re-renders.
    private async Task AssertDarkPaletteAsync() =>
        await Page.WaitForFunctionAsync(
            "() => getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-background').trim().toLowerCase() === '#1a2027'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
}
