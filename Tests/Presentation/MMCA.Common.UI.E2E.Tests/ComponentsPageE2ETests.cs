using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests;

public sealed class ComponentsPageE2ETests : GalleryAxeTestBase
{
    public ComponentsPageE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task ComponentsPage_Renders_Primitives()
    {
        await Page.GotoAndWaitForBlazorAsync("/components");

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Components Gallery" })).ToBeVisibleAsync();
        await Expect(Page.GetByText("No records found.")).ToBeVisibleAsync();
        await Expect(Page.GetByText("First item").First).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Toggle unsaved changes" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ComponentsPage_Renders_NotificationBell_AndInfiniteScroll()
    {
        await Page.GotoAndWaitForBlazorAsync("/components");

        // NotificationBell polls its (stubbed) unread count once on first render and shows the icon button.
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Notification inbox" })).ToBeVisibleAsync();
        await Expect(Page.GetByText("Mobile infinite-scroll list")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ComponentsPage_DeleteConfirmation_ShowsDialog_AndConfirms()
    {
        await Page.GotoAndWaitForBlazorAsync("/components");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Delete sample" }).ClickAsync();

        // Stable hook on the shared DeleteConfirmation YesButton — survives MudBlazor markup churn.
        var confirm = Page.GetByTestId("confirm-delete");
        await Expect(confirm).ToBeVisibleAsync();
        await confirm.ClickAsync();
        await Expect(confirm).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task ComponentsPage_HasNoWcag21AaViolations()
    {
        await Page.GotoAndWaitForBlazorAsync("/components");

        // Broadened to also cover the loading (aria-busy / named progressbar) and error (alert) primitive
        // states added to the showcase. Adding the loading state here surfaced and fixed a real defect:
        // PageLoadingState carried a prohibited aria-label on a bare div and an anonymous progressbar.
        // (Dark-mode contrast is NOT gated here yet: the dark palette's filled-primary button label and
        // error-alert text currently fail WCAG AA contrast — a §20 dark-palette tuning item tracked in
        // ACCESSIBILITY.md, not a §21 component-markup gap.)
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }
}
