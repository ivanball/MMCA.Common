using AwesomeAssertions;
using static MMCA.Common.UI.Tests.Infrastructure.CssStylesheet;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// The MAUI WebView host rules app.css owns, so a head's index.html needs no stylesheet of its own for
/// them: the status-bar spacer sized by the inset or the native fallback theme-prepaint.js sets, painted
/// above the in-page chrome but below every MudBlazor overlay (the stacking defect a z-index of 1 let
/// through: the scrolling hamburger drew over the strip), and the loading placeholder on the brand token
/// rather than a hard-coded hex. The geometry itself is a device check.
/// </summary>
public sealed class MauiHostRulesTests
{
    private static readonly string Css = CssComment.Replace(ReadEmbedded("app.css"), string.Empty);

    [Fact]
    public void StatusBarSpacer_IsSizedByTheInsetOrTheNativeFallback()
    {
        var body = SingleRuleBody(".status-bar-safe-area");

        body.Should().Contain("height: max(env(safe-area-inset-top, 0px), var(--mmca-status-bar-fallback, 0px))");
        body.Should().Contain("position: sticky");
        body.Should().Contain("background-color: var(--mmca-sidebar-bg-dark)");
    }

    [Fact]
    public void StatusBarSpacer_PaintsAboveTheChromeAndBelowEveryMudOverlay() =>
        SingleRuleBody(".status-bar-safe-area").Should().Contain("z-index: calc(var(--mud-zindex-drawer, 1100) - 1)");

    [Fact]
    public void PrepaintScript_SetsTheFallbackTheSpacerReads() =>
        ReadEmbedded("theme-prepaint.js").Should().Contain("'--mmca-status-bar-fallback'");

    [Fact]
    public void LoadingSpinner_UsesTheBrandToken_AndItsKeyframesExist()
    {
        var body = SingleRuleBody(".host-loading-spinner");

        body.Should().Contain("border-top-color: var(--mmca-primary)");
        body.Should().Contain("animation: host-loading-spin");
        Css.Should().Contain("@keyframes host-loading-spin");
    }

    private static string SingleRuleBody(string selector)
    {
        var bodies = RuleBodies(Css, candidate => string.Equals(candidate, selector, StringComparison.Ordinal));
        bodies.Should().ContainSingle($"app.css must declare exactly one `{selector}` rule");
        return bodies[0];
    }
}
