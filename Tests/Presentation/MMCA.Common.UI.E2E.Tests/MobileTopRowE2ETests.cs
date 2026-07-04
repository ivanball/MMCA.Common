using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests;

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
}
