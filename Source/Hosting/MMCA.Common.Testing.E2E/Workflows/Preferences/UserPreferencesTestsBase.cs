using AwesomeAssertions;
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
/// Selectors mirror the gallery's MobileTopRowE2ETests exactly: the controls are located by
/// accessible name / title inside their container (the app bar's .appbar-icon-actions on desktop,
/// NavMenu's .toprow-actions on phones) — the MudMenu activator does NOT expose a literal
/// aria-label attribute, so raw CSS attribute selectors do not match it.
/// </summary>
public abstract class UserPreferencesTestsBase : E2ETestBase
{
    // MudBlazor emits the palette as CSS variables on :root; depending on version the value is the
    // raw hex ("#1a2027") or its rgba form ("rgba(26,32,39,1)"). Accept either, whitespace-free.
    private const string DarkBackgroundProbeScript =
        "() => { const v = getComputedStyle(document.documentElement)" +
        ".getPropertyValue('--mud-palette-background').replace(/\\s+/g, '').toLowerCase();" +
        " return v === '#1a2027' || v === 'rgba(26,32,39,1)'; }";

    protected UserPreferencesTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    // Desktop container: the app bar's action cluster (NavMenu's mobile duplicate is display:none
    // at >=1024px but still in the DOM, so container scoping is what disambiguates).
    private ILocator DesktopActions => Page.Locator(".appbar-icon-actions").First;

    private ILocator MobileActions => Page.Locator(".toprow-actions");

    [Fact]
    public async Task CultureSwitch_ToSpanish_ShouldLocalizeAndPersist()
    {
        // Arrange — anonymous /login renders the English probe.
        await Page.GotoAndWaitForBlazorAsync("/login").ConfigureAwait(false);
        await Expect(Page.GetByText("Welcome Back")).ToBeVisibleAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);

        // Act — pick Español. The switcher forceLoad-navigates through GET /culture/set (which
        // writes the .AspNetCore.Culture cookie) and LocalRedirects back to /login; the Spanish
        // probe assertion below auto-waits across that navigation.
        await DesktopActions.GetByRole(AriaRole.Button, new() { Name = "Language" }).ClickAsync().ConfigureAwait(false);

        // MudMenu items render as .mud-menu-item (NOT .mud-list-item, and with no menuitem role) —
        // verified against the live gallery DOM; the popover carries .mud-popover-open once Blazor
        // interactivity has attached (SSR markup alone never opens it).
        await Page.Locator(".mud-popover-open .mud-menu-item", new() { HasText = "Español" })
            .ClickAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);

        // Assert — the probe is Spanish, and stays Spanish across a fresh full page load (cookie
        // persistence, not just the in-flight request).
        await Expect(Page.GetByText("Bienvenido de nuevo")).ToBeVisibleAsync(new() { Timeout = 30_000 }).ConfigureAwait(false);
        await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load }).ConfigureAwait(false);
        await Expect(Page.GetByText("Bienvenido de nuevo")).ToBeVisibleAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);
    }

    [Fact]
    public async Task ThemeToggle_ToDark_ShouldApplyAndPersist()
    {
        await Page.GotoAndWaitForBlazorAsync("/login").ConfigureAwait(false);

        // Act — toggle dark via the title-stable button (its aria-label flips with state, its title
        // does not). No navigation: MudThemeProvider re-emits its CSS variables in place.
        await DesktopActions.GetByTitle("Toggle light/dark theme").ClickAsync().ConfigureAwait(false);

        // Assert — the palette background variable flips to the PaletteDark value and the choice is
        // persisted (theme.js writes the mmca_theme cookie + localStorage mirror), then survives a
        // fresh full page load.
        await AssertDarkPaletteAsync().ConfigureAwait(false);
        var persisted = await Page.EvaluateAsync<string?>(
            "() => { try { return localStorage.getItem('mmca_theme'); } catch { return null; } }").ConfigureAwait(false);
        persisted.Should().Be("dark");

        await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load }).ConfigureAwait(false);
        await AssertDarkPaletteAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task MobileViewport_CultureAndTheme_ShouldBeReachable()
    {
        // Pin the v1.103.0 mobile-parity fix in the real app: below 1024px the app bar is hidden
        // and the controls must come from NavMenu's top row instead.
        await Page.SetViewportSizeAsync(390, 844).ConfigureAwait(false);
        await Page.GotoAndWaitForBlazorAsync("/login").ConfigureAwait(false);

        await Expect(MobileActions.GetByRole(AriaRole.Button, new() { Name = "Language" }))
            .ToBeVisibleAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);

        // The mobile theme toggle must actually work, not merely render.
        await MobileActions.GetByTitle("Toggle light/dark theme").ClickAsync().ConfigureAwait(false);
        await AssertDarkPaletteAsync().ConfigureAwait(false);
    }

    private async Task AssertDarkPaletteAsync() =>
        await Page.WaitForFunctionAsync(
            DarkBackgroundProbeScript,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 }).ConfigureAwait(false);
}
