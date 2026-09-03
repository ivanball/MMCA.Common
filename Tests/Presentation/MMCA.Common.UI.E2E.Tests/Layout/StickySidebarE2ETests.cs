using System.Globalization;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.Layout;

/// <summary>
/// Regression gate for the desktop sidebar staying pinned while the page scrolls. The sidebar is
/// exactly one viewport tall and relies on <c>position: sticky</c> to cover the full height of a
/// longer page; when it stops sticking, its dark background simply ends one viewport down and the
/// page background shows beside the content.
/// <para>
/// Sticky resolves against the nearest ancestor scroll container, so ANY ancestor picking up a
/// non-visible overflow silently disables it. Two rules did: <c>html, body { overflow-y: auto }</c>
/// in app.css, and <c>.page { overflow-x: hidden }</c> in MainLayout.razor.css (a non-visible
/// overflow-x forces the computed overflow-y from visible to auto). Both ancestors are content-
/// sized and therefore never scroll themselves, so the sidebar resolved against a dead scrollport
/// and scrolled away with the document. These assertions pin the behaviour rather than the CSS, so
/// they catch any future ancestor that reintroduces a scrollport.
/// </para>
/// </summary>
public sealed class StickySidebarE2ETests : GalleryAxeTestBase
{
    private const string SidebarTop = "() => Math.round(document.querySelector('aside.sidebar').getBoundingClientRect().top)";

    public StickySidebarE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task DesktopViewport_SidebarStaysPinned_WhenPageIsScrolled()
    {
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync("/components");
        await Page.WaitForLoadStateAsync();

        // Guard the premise: a page shorter than the viewport cannot demonstrate the defect.
        var scrollable = await Page.EvaluateAsync<int>(
            "() => document.documentElement.scrollHeight - window.innerHeight");
        Assert.True(
            scrollable > 200,
            string.Create(CultureInfo.InvariantCulture, $"Gallery page is not tall enough to scroll ({scrollable}px)."));

        Assert.Equal(0, await Page.EvaluateAsync<int>(SidebarTop));

        await Page.EvaluateAsync("() => window.scrollTo(0, document.documentElement.scrollHeight)");
        await Page.WaitForTimeoutAsync(250);

        // Pinned means the sidebar top is still flush with the viewport top, so its background
        // continues to cover the full viewport height at the bottom of the page.
        Assert.Equal(0, await Page.EvaluateAsync<int>(SidebarTop));
    }

    [Fact]
    public async Task NoAncestorOfTheSidebar_EstablishesAScrollport()
    {
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync("/components");
        await Page.WaitForLoadStateAsync();

        var offenders = await Page.EvaluateAsync<string[]>(
            "() => { const out = []; for (let e = document.querySelector('aside.sidebar').parentElement; e; e = e.parentElement) { const c = getComputedStyle(e); if (c.overflowY !== 'visible' || (c.overflowX !== 'visible' && c.overflowX !== 'clip')) { out.push(e.tagName + '.' + ((e.className || '').toString().split(' ')[0]) + ' overflow: ' + c.overflowX + ' ' + c.overflowY); } } return out; }");

        Assert.Empty(offenders);
    }
}
