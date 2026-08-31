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

    /// <summary>
    /// The measurement flow lives in the shipped <c>MeasureWebVitalsAsync</c> extension (install the
    /// observers BEFORE the navigation, load, collect, write the artifact, assert), so this suite no
    /// longer hand-rolls it. FCP is pinned to the LCP ceiling rather than left on the package default:
    /// FCP always precedes LCP, so this keeps the effective gate exactly the three metrics the suite has
    /// always gated on and its CI history comparable. INP stays on the default and is skipped anyway,
    /// since neither case drives an interaction.
    /// </summary>
    private static readonly WebVitalsBudget Budget =
        new(Lcp: LcpBudgetMs, Fcp: LcpBudgetMs, Ttfb: TtfbBudgetMs, Cls: ClsBudget);

    public WebVitalsE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task LoginPage_CoreWebVitals_WithinBudget()
    {
        var sample = await Page.MeasureWebVitalsAsync("gallery-login", "/login", Budget);

        AssertSomethingWasMeasured(sample);
    }

    [Fact]
    public async Task ComponentsPage_CoreWebVitals_WithinBudget()
    {
        var sample = await Page.MeasureWebVitalsAsync("gallery-components", "/components", Budget);

        AssertSomethingWasMeasured(sample);
    }

    [Fact]
    public async Task GridPage_CoreWebVitals_WithinBudget()
    {
        var sample = await Page.MeasureWebVitalsAsync("gallery-grid", "/grid", Budget);

        AssertSomethingWasMeasured(sample);
    }

    /// <summary>
    /// Guards against a vacuous pass. An all-zero sample (collector never installed, navigation never
    /// happened) is inside every ceiling, so the budget alone cannot tell "fast" from "not measured".
    /// LCP and CLS are Chromium-only, so the cross-engine floor is TTFB or FCP.
    /// </summary>
    private static void AssertSomethingWasMeasured(WebVitalsSample sample) =>
        Assert.True(
            sample.Ttfb > 0 || sample.Fcp > 0,
            "No TTFB or FCP was recorded, so the budget assertion measured nothing.");
}
