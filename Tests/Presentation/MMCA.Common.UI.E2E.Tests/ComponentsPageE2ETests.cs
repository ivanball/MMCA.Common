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
        await Expect(Page.GetByText("First item")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Toggle unsaved changes" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ComponentsPage_HasNoWcag21AaViolations()
    {
        await Page.GotoAndWaitForBlazorAsync("/components");

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }
}
