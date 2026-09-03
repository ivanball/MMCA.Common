using MMCA.Common.Testing.E2E.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.WebVitals;

/// <summary>
/// Pure unit coverage for the shared budget mechanics in <see cref="WebVitalsBudget"/> (no browser is
/// started). It is the one place the behaviours that only ever fire on a REGRESSION can be exercised: that
/// each metric actually fails when it exceeds its ceiling, and that a 0 INP (no interaction cleared the
/// 16 ms event threshold) is skipped rather than read as a pass or a failure.
/// </summary>
public sealed class WebVitalsBudgetTests
{
    private static WebVitalsSample Good() => new() { Lcp = 600, Fcp = 400, Ttfb = 30, Cls = 0.005, Inp = 32 };

    [Fact]
    public void Defaults_AreTheCoreWebVitalsGoodBand()
    {
        var budget = new WebVitalsBudget();

        Assert.Equal(2500, budget.Lcp);
        Assert.Equal(1800, budget.Fcp);
        Assert.Equal(800, budget.Ttfb);
        Assert.Equal(0.1, budget.Cls);
        Assert.Equal(500, budget.Inp);
    }

    [Fact]
    public void AssertWithinBudget_Passes_ForASampleInsideEveryCeiling() =>
        new WebVitalsBudget().AssertWithinBudget(Good(), "unit", "/");

    [Fact]
    public void AssertWithinBudget_WritesTheSampleLine()
    {
        var lines = new List<string>();

        new WebVitalsBudget().AssertWithinBudget(Good(), "sessions", "/conference/sessions", lines.Add);

        var line = Assert.Single(lines);
        Assert.Equal(
            "[web-vitals:sessions] path=/conference/sessions LCP=600ms FCP=400ms CLS=0.005 TTFB=30ms INP-sample=32ms",
            line);
    }

    [Theory]
    [InlineData(3000, 400, 30, 0.005, 32)] // LCP over
    [InlineData(600, 2000, 30, 0.005, 32)] // FCP over
    [InlineData(600, 400, 900, 0.005, 32)] // TTFB over
    [InlineData(600, 400, 30, 0.2, 32)] // CLS over
    [InlineData(600, 400, 30, 0.005, 700)] // INP sample over
    public void AssertWithinBudget_Fails_WhenAnyMetricExceedsItsCeiling(double lcp, double fcp, double ttfb, double cls, double inp)
    {
        var sample = new WebVitalsSample { Lcp = lcp, Fcp = fcp, Ttfb = ttfb, Cls = cls, Inp = inp };

        Assert.ThrowsAny<Exception>(() => new WebVitalsBudget().AssertWithinBudget(sample, "unit", "/"));
    }

    [Fact]
    public void AssertWithinBudget_SkipsInp_WhenNoInteractionSampleWasRecorded()
    {
        var sample = Good() with { Inp = 0 };

        new WebVitalsBudget(Inp: 1).AssertWithinBudget(sample, "unit", "/");
    }

    [Fact]
    public void AssertWithinBudget_HonoursACallerSuppliedBudget()
    {
        var sample = Good() with { Lcp = 5000 };

        new WebVitalsBudget(Lcp: 8000).AssertWithinBudget(sample, "unit", "/");
        Assert.ThrowsAny<Exception>(() => new WebVitalsBudget(Lcp: 4000).AssertWithinBudget(sample, "unit", "/"));
    }
}
