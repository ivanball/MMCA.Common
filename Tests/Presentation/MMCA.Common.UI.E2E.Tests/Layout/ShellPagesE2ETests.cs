using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Layout;

/// <summary>
/// Axe gate for the three framework-owned shell pages the suite never scanned: the home page and the
/// two status pages (404 and 403). They are the pages a consumer is least likely to override and the
/// ones a visitor is most likely to land on out of context, and all three shipped without a
/// level-one heading, which is also what <c>Routes.razor</c>'s <c>FocusOnNavigate Selector="h1"</c>
/// needs in order to move focus on navigation at all.
/// </summary>
public sealed class ShellPagesE2ETests : GalleryAxeTestBase
{
    public ShellPagesE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/not-found")]
    [InlineData("/forbidden")]
    public async Task ShellPage_HasNoWcag21AaViolations(string route)
    {
        await Page.GotoAndWaitForBlazorAsync(route);

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/not-found")]
    [InlineData("/forbidden")]
    public async Task ShellPage_HasExactlyOneLevelOneHeading(string route)
    {
        // axe's page-has-heading-one is a best-practice rule and therefore outside the gate's tag
        // set, so the requirement is asserted here instead of being silently invisible to CI.
        await Page.GotoAndWaitForBlazorAsync(route);

        var heading = Page.Locator("h1");
        await Expect(heading).ToHaveCountAsync(1);
        await Expect(heading).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ForbiddenPage_KeepsTheHeadingRole_AndAlertsOnTheMessage()
    {
        // role="alert" used to sit ON the h1, and an explicit role replaces the implicit one, so the
        // page announced an alert and had no heading at all.
        await Page.GotoAndWaitForBlazorAsync("/forbidden");

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToBeVisibleAsync();
        await Expect(Page.Locator("h1")).Not.ToHaveAttributeAsync("role", "alert");
        await Expect(Page.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
    }
}
