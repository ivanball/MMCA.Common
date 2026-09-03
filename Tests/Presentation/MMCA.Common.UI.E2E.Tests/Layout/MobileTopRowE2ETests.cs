using System.Globalization;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Layout;

/// <summary>
/// Regression gate for mobile parity of the culture + theme controls (rubric §22 / ADR-027/028).
/// The shared layout hides the whole <c>MudAppBar</c> below 1024px, so on phones those controls only
/// exist if <c>NavMenu</c>'s mobile top-row renders them; they were originally app-bar-only and thus
/// invisible on every phone, for anonymous and signed-in users alike. These tests pin both sides of
/// the breakpoint: at phone width the top-row exposes both controls (without authentication); at
/// desktop width the top-row duplicate is hidden and the app bar carries them instead.
/// </summary>
public sealed class MobileTopRowE2ETests : GalleryAxeTestBase
{
    public MobileTopRowE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task PhoneViewport_ShowsCultureAndThemeControls_InNavMenuTopRow()
    {
        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync("/");
        await Page.WaitForLoadStateAsync();

        var topRow = Page.Locator(".toprow-actions");

        // The gallery host runs anonymously, so this also proves the controls are not auth-gated.
        await Expect(topRow.GetByRole(AriaRole.Button, new() { Name = "Language" })).ToBeVisibleAsync();
        await Expect(topRow.GetByTitle("Toggle light/dark theme")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task PhoneViewport_TopRowActions_DoNotOverlapTheHamburger()
    {
        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync("/");
        await Page.WaitForLoadStateAsync();

        // The theme toggle is the last item in the signed-out top-row, so it is the one that lands on
        // the hamburger when .toprow-actions is allowed to shrink below its content width. The gallery
        // host is anonymous, which is exactly the state that used to overlap: signed in, the user-name
        // span absorbs the squeeze by ellipsizing and hides the defect.
        var themeToggle = Page.Locator(".toprow-actions").GetByTitle("Toggle light/dark theme");
        var hamburger = Page.Locator(".navbar-toggler");

        // Settle both before measuring: BoundingBoxAsync does not auto-wait, so reading it straight
        // after the navigation returns null whenever the interactive render has not landed yet.
        await Expect(themeToggle).ToBeVisibleAsync();
        await Expect(hamburger).ToBeVisibleAsync();

        var themeBox = await themeToggle.BoundingBoxAsync();
        var hamburgerBox = await hamburger.BoundingBoxAsync();

        Assert.NotNull(themeBox);
        Assert.NotNull(hamburgerBox);
        var themeRight = (themeBox.X + themeBox.Width).ToString(CultureInfo.InvariantCulture);
        var hamburgerLeft = hamburgerBox.X.ToString(CultureInfo.InvariantCulture);
        Assert.True(
            themeBox.X + themeBox.Width <= hamburgerBox.X,
            $"Theme toggle (right edge {themeRight}) overlaps the hamburger (left edge {hamburgerLeft}).");
    }

    [Fact]
    public async Task DesktopViewport_HidesTopRowDuplicate_AndAppBarCarriesTheControls()
    {
        await Page.SetViewportSizeAsync(1280, 800);
        await Page.GotoAsync("/");
        await Page.WaitForLoadStateAsync();

        // Desktop CSS hides the whole mobile top-row actions span, so the controls never render twice.
        await Expect(Page.Locator(".toprow-actions")).ToBeHiddenAsync();

        var appBar = Page.Locator(".appbar-icon-actions").First;
        await Expect(appBar.GetByRole(AriaRole.Button, new() { Name = "Language" })).ToBeVisibleAsync();
        await Expect(appBar.GetByTitle("Toggle light/dark theme")).ToBeVisibleAsync();
    }

    /// <summary>
    /// The signed-in user name belongs in exactly one place on a phone: the hamburger menu, above
    /// Logout. The top-row actions span only ever renders below 1024px, so a copy there was always a
    /// duplicate of the menu entry, and it competed for room on the narrowest row in the layout.
    /// </summary>
    [Fact]
    public async Task PhoneViewport_ShowsUserName_OnlyInTheHamburgerMenu()
    {
        await Page.Context.AddCookiesAsync(
        [
            new Cookie { Name = "gallery_auth", Value = "1", Url = BaseUrl },
        ]);

        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync("/");
        await Page.WaitForLoadStateAsync();

        // Premise: the gallery cookie really did sign this scan in, so an absent name below is the
        // top-row rule and not an anonymous render.
        await Expect(Page.Locator(".nav-auth-section")).ToContainTextAsync("Gallery Visitor");

        await Expect(Page.Locator(".toprow-actions")).ToBeVisibleAsync();
        await Expect(Page.Locator(".toprow-actions")).Not.ToContainTextAsync("Gallery Visitor");

        // Opening the menu is what surfaces the name to the user: .nav-scrollable is collapsed to
        // max-height 0 until the toggler is checked.
        await Page.Locator(".navbar-toggler").CheckAsync();
        await Expect(Page.Locator(".nav-user-identity")).ToBeVisibleAsync();
        await Expect(Page.Locator(".nav-user-identity")).ToContainTextAsync("Gallery Visitor");
    }
}
