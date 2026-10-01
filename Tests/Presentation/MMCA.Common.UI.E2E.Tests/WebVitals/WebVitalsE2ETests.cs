using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.WebVitals;

/// <summary>
/// Front-end performance budgets for the shared UI surface (rubric §23), measured with the shipped
/// <see cref="WebVitalsCollector"/> against the in-process gallery host. The gallery is backend-less
/// and local, so unlike the consumer suites (which measure a full Aspire stack under CI contention)
/// these numbers isolate the shared chrome + pages themselves.
/// </summary>
public sealed class WebVitalsE2ETests : GalleryAxeTestBase
{
    private const double LcpBudgetMs = 2500;
    private const double FcpBudgetMs = 1800;
    private const double TtfbBudgetMs = 800;
    private const double ClsBudget = 0.1;
    private const double InpBudgetMs = 200;

    /// <summary>
    /// Records every Event Timing entry the page produces, installed alongside the shipped collector.
    /// The collector's own observer only sees Event Timing entries over the 16 ms threshold, so a fast
    /// interaction leaves its INP at 0 and the budget skips it; first-input entries are reported
    /// whatever their duration, so this probe proves an interaction was actually timed. Support is read
    /// from <c>PerformanceObserver.supportedEntryTypes</c> at runtime, never assumed per engine.
    /// Kept as one concatenated string (no multi-line raw literal) to stay clear of MA0136.
    /// </summary>
    private const string InpProbeScript =
        "window.__inpProbe = { supported: false, count: 0, max: 0 };" +
        "try { const types = (typeof PerformanceObserver !== 'undefined' && PerformanceObserver.supportedEntryTypes) || [];" +
        " const record = (l) => { for (const en of l.getEntries()) { window.__inpProbe.count++;" +
        " if (en.duration > window.__inpProbe.max) { window.__inpProbe.max = en.duration; } } };" +
        " if (types.includes('first-input')) { window.__inpProbe.supported = true; new PerformanceObserver(record).observe({ type: 'first-input', buffered: true }); }" +
        " if (types.includes('event')) { window.__inpProbe.supported = true; new PerformanceObserver(record).observe({ type: 'event', buffered: true, durationThreshold: 16 }); }" +
        "} catch (e) { }";

    /// <summary>
    /// The Core Web Vitals "good" band (LCP 2500 / FCP 1800 / CLS 0.1), the TTFB ceiling, and the
    /// "good" INP ceiling of 200 ms. These replace the interim 4000/3000/1500/0.1/500 set after the
    /// gallery measured well inside them; the numbers move on measured evidence, never on a guess.
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
        var sample = await Page.MeasureWebVitalsAsync("gallery-login", "/login", Budget, WriteLine);

        AssertSomethingWasMeasured(sample);
    }

    [Fact]
    public async Task ComponentsPage_CoreWebVitals_WithinBudget()
    {
        var sample = await Page.MeasureWebVitalsAsync("gallery-components", "/components", Budget, WriteLine);

        AssertSomethingWasMeasured(sample);
    }

    [Fact]
    public async Task GridPage_CoreWebVitals_WithinBudget()
    {
        var sample = await Page.MeasureWebVitalsAsync("gallery-grid", "/grid", Budget, WriteLine);

        AssertSomethingWasMeasured(sample);
    }

    /// <summary>
    /// Drives one real interaction on each gallery page and requires an INP sample within budget. On
    /// an engine whose <c>PerformanceObserver</c> exposes neither the Event Timing entry type nor the
    /// first-input one the test is skipped with that reason; everywhere else an
    /// interaction that produced no timing entry fails, so a silent zero cannot pass as fast.
    /// </summary>
    /// <param name="label">The artifact label.</param>
    /// <param name="path">The gallery page.</param>
    /// <returns>A task that completes when the page is measured.</returns>
    [Theory]
    [InlineData("gallery-login-inp", "/login")]
    [InlineData("gallery-components-inp", "/components")]
    [InlineData("gallery-grid-inp", "/grid")]
    public async Task Interaction_IsSampled_AndWithinInpBudget(string label, string path)
    {
        await Page.AddInitScriptAsync(InpProbeScript);

        var sample = await Page.MeasureWebVitalsWithInteractionAsync(label, path, Budget, InteractAsync(path), WriteLine);
        AssertSomethingWasMeasured(sample);

        var probe = JsonSerializer.Deserialize<InpProbe>(
            await Page.EvaluateAsync<string>("() => JSON.stringify(window.__inpProbe)")) ?? new InpProbe();

        if (!probe.Supported)
        {
            Assert.Skip($"This engine's PerformanceObserver exposes neither the 'event' nor the 'first-input' entry type, so INP cannot be sampled on {path}.");
        }

        var inp = Math.Max(probe.Max, sample.Inp);
        WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[web-vitals:{label}] path={path} INP={inp:F0}ms entries={probe.Count}"));

        Assert.True(probe.Count > 0, $"The interaction on {path} produced no Event Timing entry, so INP was not sampled.");
        Assert.True(
            inp <= InpBudgetMs,
            string.Create(CultureInfo.InvariantCulture, $"INP {inp:F0} exceeded budget {InpBudgetMs} on {path}"));
    }

    /// <summary>One plain, side-effect-free interaction per page: the kind of event INP is about.</summary>
    private static Func<IPage, Task> InteractAsync(string path) => path switch
    {
        "/login" => TypeIntoEmailAsync,
        "/components" => page => page.GetByTestId("toggle-dirty").ClickAsync(),
        "/grid" => page => page.GetByText("Row 0001", new() { Exact = true }).ClickAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "No interaction is defined for this page."),
    };

    private static async Task TypeIntoEmailAsync(IPage page)
    {
        var email = page.Locator("input[autocomplete='email']");
        await email.ClickAsync();
        await email.PressSequentiallyAsync("a");
    }

    private static void WriteLine(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>
    /// Guards against a vacuous pass. An all-zero sample (collector never installed, navigation never
    /// happened) is inside every ceiling, so the budget alone cannot tell "fast" from "not measured".
    /// LCP and CLS are Chromium-only, so the cross-engine floor is TTFB or FCP.
    /// </summary>
    private static void AssertSomethingWasMeasured(WebVitalsSample sample) =>
        Assert.True(
            sample.Ttfb > 0 || sample.Fcp > 0,
            "No TTFB or FCP was recorded, so the budget assertion measured nothing.");

    private sealed record InpProbe
    {
        [JsonPropertyName("supported")] public bool Supported { get; init; }

        [JsonPropertyName("count")] public int Count { get; init; }

        [JsonPropertyName("max")] public double Max { get; init; }
    }
}
