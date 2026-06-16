using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests;

/// <summary>
/// Render-smoke + WCAG 2.1 AA accessibility scans for the shared Notification pages, rendered against
/// the gallery's stubbed notification seams (the pages are discovered from the MMCA.Common.UI assembly).
/// </summary>
public sealed class NotificationPagesE2ETests : GalleryAxeTestBase
{
    public NotificationPagesE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task NotificationHistory_Renders_AndHasNoWcag21AaViolations()
    {
        await Page.GotoAndWaitForBlazorAsync("/notifications");

        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Send New Notification" })).ToBeVisibleAsync();
        await Expect(Page.GetByText("Welcome to MMCA").First).ToBeVisibleAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task NotificationInbox_Renders_AndHasNoWcag21AaViolations()
    {
        await Page.GotoAndWaitForBlazorAsync("/notifications/inbox");

        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Mark All as Read" })).ToBeVisibleAsync();
        await Expect(Page.GetByText("Welcome to MMCA").First).ToBeVisibleAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task NotificationCompose_Renders_AndHasNoWcag21AaViolations()
    {
        await Page.GotoAndWaitForBlazorAsync("/notifications/send");

        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Send to All Recipients" })).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Title")).ToBeVisibleAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }
}
