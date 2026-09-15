using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.WebVitals;

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
    private const double LcpBudgetMs = 4000;
    private const double FcpBudgetMs = 3000;
    private const double TtfbBudgetMs = 1500;
    private const double ClsBudget = 0.1;
    private const double InpBudgetMs = 500;

    /// <summary>
    /// The measurement flow lives in the shipped <c>MeasureWebVitalsAsync</c> extension (install the
    /// observers BEFORE the navigation, load, collect, write the artifact, assert), so this suite no
    /// longer hand-rolls it.
    /// <para>
    /// These ceilings are a deliberate step down from the opening 8000/4000/0.25 set, which was loose
    /// enough to miss anything short of a catastrophe. They step down again to the package default
    /// (<c>new WebVitalsBudget()</c>: 2500/1800/800/0.1/500) after one green cross-browser cycle; the
    /// numbers move on measured CI evidence, never on a guess. LCP and CLS are Chromium-only and INP
    /// needs a real interaction, so firefox and webkit report 0 for those and
    /// <c>WebVitalsBudget.AssertWithinBudget</c> skips a zero INP rather than reading it as a pass.
    /// </para>
    /// </summary>
    private static readonly WebVitalsBudget Budget =
        new(Lcp: LcpBudgetMs, Fcp: FcpBudgetMs, Ttfb: TtfbBudgetMs, Cls: ClsBudget, Inp: InpBudgetMs);

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

    /// <summary>
    /// The one case that drives an interaction, so INP is sampled rather than skipped: the gallery's
    /// dirty toggle is a plain button click, which is exactly the kind of event the responsiveness
    /// metric is about. Non-Chromium engines record no INP and the budget skips it there.
    /// </summary>
    /// <returns>A task that completes when the page is measured.</returns>
    [Fact]
    public async Task ComponentsPage_CoreWebVitals_WithinBudget()
    {
        var sample = await Page.MeasureWebVitalsWithInteractionAsync(
            "gallery-components",
            "/components",
            Budget,
            page => page.GetByTestId("toggle-dirty").ClickAsync());

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
