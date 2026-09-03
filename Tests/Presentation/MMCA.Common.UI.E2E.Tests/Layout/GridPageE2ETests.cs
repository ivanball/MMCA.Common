using System.Globalization;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Layout;

/// <summary>
/// Real-browser coverage for the opt-in virtualized list page (<c>/grid</c> in the gallery): that the
/// windowing is actually happening (the DOM holds a small fraction of the 1,000-row data set), that the
/// grid scrolls inside its own height-bound viewport rather than the document, and that the virtualized
/// markup still passes the same WCAG 2.1 AA scan as every other gallery page.
/// </summary>
public sealed class GridPageE2ETests : GalleryAxeTestBase
{
    /// <summary>Row count of the gallery's generated data set (see <c>SampleGridData.RowCount</c>).</summary>
    private const int DataSetRowCount = 1000;

    /// <summary>
    /// Ceiling on rendered body rows. A 70vh viewport at 52px per row plus MudBlazor's overscan lands
    /// well under 50 on any CI viewport; 200 leaves generous headroom while still being a fifth of the
    /// data set, so a regression that renders everything cannot slip through.
    /// </summary>
    private const int MaxRenderedRows = 200;

    public GridPageE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task GridPage_RendersFarFewerRowsThanTheDataSet()
    {
        await Page.GotoAndWaitForBlazorAsync("/grid");

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Virtualized Grid Gallery" })).ToBeVisibleAsync();
        await Expect(Page.GetByText("Row 0001")).ToBeVisibleAsync();

        var rendered = await Page.Locator("tbody tr").CountAsync();

        Assert.True(
            rendered < MaxRenderedRows,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Expected a virtualized window under {MaxRenderedRows} rows out of {DataSetRowCount}, but {rendered} rows were rendered."));
        Assert.True(rendered > 0, "The grid rendered no rows at all, so the window assertion proves nothing.");
    }

    [Fact]
    public async Task GridPage_ScrollsInsideItsOwnViewport_AndKeepsTheWindowSmall()
    {
        await Page.GotoAndWaitForBlazorAsync("/grid");
        await Expect(Page.GetByText("Row 0001")).ToBeVisibleAsync();

        // The height-bound container is what scrolls (and what the base class tracks for scroll
        // restore); the document itself does not.
        var container = Page.Locator(".mud-table-container");
        await Expect(container).ToBeVisibleAsync();

        await container.EvaluateAsync("el => { el.scrollTop = 5000; }");
        var scrollTop = await container.EvaluateAsync<double>("el => el.scrollTop");
        Assert.True(scrollTop > 0, "The grid container did not scroll, so it is not the virtualization viewport.");

        // The window moves with the scroll; it must not accumulate.
        await Expect(Page.Locator("tbody tr").First).ToBeVisibleAsync();
        var rendered = await Page.Locator("tbody tr").CountAsync();
        Assert.True(
            rendered < MaxRenderedRows,
            string.Create(
                CultureInfo.InvariantCulture,
                $"After scrolling, {rendered} rows were rendered; the window must stay under {MaxRenderedRows}."));
    }

    [Fact]
    public async Task GridPage_HasNoWcag21AaViolations()
    {
        await Page.GotoAndWaitForBlazorAsync("/grid");
        await Expect(Page.GetByText("Row 0001")).ToBeVisibleAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }
}
