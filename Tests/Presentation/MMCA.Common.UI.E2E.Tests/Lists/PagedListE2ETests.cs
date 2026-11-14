using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Lists;

/// <summary>
/// Pager state on the shared list base (<c>DataGridListPageBase</c>), through the gallery's
/// <c>/grid/paged</c> list (ListPageShell, as an app builds its admin lists) over 60 canned rows, against the real prerender and interactive passes
/// (a component test never sees the prerender hand-off). <c>p</c> is the zero-based page index, so
/// <c>p=1&amp;ps=25</c> is users 26 to 50.
/// </summary>
public sealed class PagedListE2ETests : GalleryAxeTestBase
{
    // A restored descending sort on top of the page: every list page seeds a default column sort, and
    // putting the restored one on the header reloads the grid (the shape Run 17 failed with). The stub
    // ignores the sort, so the page's rows stay in id order.
    private const string SecondPageOf25 = "/grid/paged?p=1&ps=25&s=Email&sd=desc&q=user";

    public PagedListE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    /// <summary>Run 17 O-85: a list restored from a URL with <c>p=N</c> shows page N's rows and keeps <c>p</c>.</summary>
    [Fact]
    public async Task AListRestoredFromTheUrl_ShowsThatPagesRows_AndKeepsThePageInTheUrl()
    {
        await Page.SetViewportSizeAsync(1280, 900);
        await Page.GotoAndWaitForBlazorAsync(SecondPageOf25);

        await ExpectSecondPageOf25Async();
    }

    /// <summary>
    /// Run 17 O-73: widening from the phone cards back to the grid keeps the page and the page size,
    /// and the URL keeps <c>p</c> and <c>ps</c>.
    /// </summary>
    [Fact]
    public async Task WideningFromThePhoneCardsBackToTheGrid_KeepsThePageAndThePageSize()
    {
        await Page.SetViewportSizeAsync(1280, 900);
        await Page.GotoAndWaitForBlazorAsync(SecondPageOf25);
        await ExpectSecondPageOf25Async();

        await Page.SetViewportSizeAsync(390, 844);
        await Expect(Page.Locator(".paged-card").First).ToBeVisibleAsync();
        await Expect(Page.Locator("tbody .mud-table-row")).ToHaveCountAsync(0);

        await Page.SetViewportSizeAsync(1280, 900);
        await ExpectSecondPageOf25Async();
    }

    private async Task ExpectSecondPageOf25Async()
    {
        var rows = Page.Locator("tbody .mud-table-row");
        await Expect(rows.First).ToContainTextAsync("user26@example.com");
        await Expect(rows).ToHaveCountAsync(25);
        var query = new Uri(Page.Url).Query.TrimStart('?').Split('&');
        Assert.Contains("p=1", query);
        Assert.Contains("ps=25", query);
        Assert.Contains("sd=desc", query);
    }
}
