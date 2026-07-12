using System.Globalization;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests;

/// <summary>
/// Front-end performance budgets for the shared UI surface (rubric §23), measured with the shipped
/// <see cref="WebVitalsCollector"/> against the in-process gallery host. The gallery is backend-less
/// and local, so unlike the consumer suites (which measure a full Aspire stack under CI contention)
/// these numbers isolate the shared chrome + pages themselves; the budgets are still generous enough
/// to absorb CI-runner variance while failing the build on a catastrophic regression (an accidental
/// render loop, a giant synchronous asset, a layout-shifting chrome change).
/// </summary>
public sealed class WebVitalsE2ETests : GalleryAxeTestBase
{
    private const double LcpBudgetMs = 8000;
    private const double TtfbBudgetMs = 4000;
    private const double ClsBudget = 0.25;

    public WebVitalsE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task LoginPage_CoreWebVitals_WithinBudget() =>
        await MeasureAsync("gallery-login", "/login");

    [Fact]
    public async Task ComponentsPage_CoreWebVitals_WithinBudget() =>
        await MeasureAsync("gallery-components", "/components");

    private async Task MeasureAsync(string label, string path)
    {
        await WebVitalsCollector.InstallAsync(Page);
        await Page.GotoAndWaitForBlazorAsync(path);

        var sample = await WebVitalsCollector.CollectAsync(Page);
        await WebVitalsCollector.WriteArtifactAsync(label, path, sample);

        Assert.True(sample.Lcp <= LcpBudgetMs, string.Create(CultureInfo.InvariantCulture, $"LCP {sample.Lcp:F0}ms exceeded budget {LcpBudgetMs}ms on {path}"));
        Assert.True(sample.Ttfb <= TtfbBudgetMs, string.Create(CultureInfo.InvariantCulture, $"TTFB {sample.Ttfb:F0}ms exceeded budget {TtfbBudgetMs}ms on {path}"));
        Assert.True(sample.Cls <= ClsBudget, string.Create(CultureInfo.InvariantCulture, $"CLS {sample.Cls:F3} exceeded budget {ClsBudget} on {path}"));
    }
}
