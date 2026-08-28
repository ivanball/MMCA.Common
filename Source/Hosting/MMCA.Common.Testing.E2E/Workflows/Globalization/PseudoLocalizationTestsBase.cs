using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Testing.E2E.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.Testing.E2E.Workflows.Globalization;

/// <summary>
/// One page under the pseudo-localization gate: the path to load, an en-US probe string owned by a
/// resource rendered on it, and an optional selector that proves the page's async content settled.
/// </summary>
/// <param name="Path">The path to load.</param>
/// <param name="EnglishProbe">
/// A string KNOWN to come from a resource rendered on this page. Under the pseudo culture every
/// letter gains a combining accent, so the plain en-US form appearing means some render path bypassed
/// the localizer.
/// </param>
/// <param name="SettleSelector">
/// A selector whose visibility proves the page's async content rendered (a seeded grid row, a card, a
/// static section). Null waits only for the loading indicator to clear.
/// </param>
public sealed record PseudoLocalizedPage(string Path, string EnglishProbe, string? SettleSelector = null);

/// <summary>
/// Pseudo-localization fitness gate for an app's own pages (ADR-027 section 8 / rubric section 27),
/// authored once here and re-run as a thin sealed subclass per consumer repo. Each declared page is
/// loaded under the <c>qps-Ploc</c> pseudo culture and asserted three ways:
/// <list type="number">
/// <item><description>at least one VISIBLE bracket sentinel renders, proving the app's own resx
/// resources make the full round trip (resx -> IStringLocalizer -> PseudoStringLocalizer -> markup)
/// rather than being hard-coded;</description></item>
/// <item><description>the page does not overflow horizontally under the pseudo pass's roughly 40%
/// text expansion, the rubric's layout-tolerance criterion;</description></item>
/// <item><description>the page's en-US probe does NOT render in plain English, so a regression that
/// bypasses the localizer fails the gate instead of shipping silently.</description></item>
/// </list>
/// A reverse leak guard asserts the sentinel is absent under the default culture (and, by default,
/// that the probe IS plain English there), so pseudo text can never ship to a real locale unnoticed
/// and the probes stay in sync with their resources.
/// </summary>
/// <remarks>
/// Activation is the culture COOKIE via <c>GET /culture/set</c> (the shared <c>MapCultureEndpoint</c>
/// the culture switcher uses), not a query-string culture provider: a Blazor Server circuit takes its
/// culture from the request that STARTS the circuit, which carries cookies but not the original
/// page's query string, so a query-string-only activation would pseudo-localize the prerender and
/// then revert on hydration. <c>qps-Ploc</c> is allowlisted in Development only
/// (<c>UseCommonRequestLocalization</c> / <c>MapCultureEndpoint</c>), which is what an Aspire-launched
/// E2E stack runs, so this gate needs no host change. Public pages are the right subjects: no login
/// keeps the gate robust.
/// </remarks>
public abstract class PseudoLocalizationTestsBase : E2ETestBase
{
    protected PseudoLocalizationTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>The pages this app puts under the gate, each with its own en-US probe.</summary>
    protected abstract IReadOnlyList<PseudoLocalizedPage> ScannedPages { get; }

    /// <summary>
    /// The pseudo locale to activate. Mirrors <c>SupportedCultures.PseudoLocale</c>; stated here
    /// because this shipped fixture library deliberately does not reference MMCA.Common.Shared.
    /// </summary>
    protected virtual string PseudoLocale => "qps-Ploc";

    /// <summary>The PseudoLocalizer bracket-sentinel prefix, which opens every transformed string.</summary>
    protected virtual string Sentinel => "[!!";

    /// <summary>
    /// Horizontal-overflow tolerance in CSS pixels. One pixel absorbs sub-pixel layout rounding
    /// without admitting a genuine sideways scrollbar.
    /// </summary>
    protected virtual int OverflowTolerancePx => 1;

    /// <summary>
    /// Whether the en-US leak probe is searched in the page's VISIBLE text only (body innerText)
    /// rather than in the whole document. The default (false) searches the whole document, which also
    /// covers probes that live in attributes such as an input placeholder. Set true for an app whose
    /// probes are visible text and whose markup legitimately carries the plain form elsewhere.
    /// </summary>
    protected virtual bool ProbeVisibleTextOnly => false;

    /// <summary>
    /// Whether the default-culture guard also asserts each probe IS present in plain English. On by
    /// default: it is what keeps the probes honest, since a probe that drifted from its resource value
    /// would otherwise make the pseudo test's leak assertion pass vacuously.
    /// </summary>
    protected virtual bool AssertProbePresentUnderDefaultCulture => true;

    /// <summary>Per-step wait budget in milliseconds.</summary>
    protected virtual float Timeout => 15_000;

    [Fact]
    public async Task PseudoLocale_RendersSentinel_WithoutEnUsLeak_AndDoesNotOverflowHorizontally()
    {
        ScannedPages.Should().NotBeEmpty(
            "at least one page must be declared, or the pseudo-localization gate verifies nothing");

        foreach (var scanned in ScannedPages)
        {
            // Activate qps-Ploc exactly like the app's culture switcher does: GET /culture/set writes
            // the .AspNetCore.Culture cookie and LocalRedirects to the target page, so BOTH the SSR
            // prerender and the interactive circuit render under the pseudo culture.
            await Page.GotoAndWaitForBlazorAsync(
                $"/culture/set?culture={PseudoLocale}&redirectUri={Uri.EscapeDataString(scanned.Path)}").ConfigureAwait(false);

            await SettleAsync(scanned.SettleSelector).ConfigureAwait(false);

            // (a) At least one VISIBLE sentinel: the pseudo pipeline is active end to end on this page.
            await Expect(Page.GetByText(Sentinel).Filter(new() { Visible = true }).First)
                .ToBeVisibleAsync(new() { Timeout = Timeout }).ConfigureAwait(false);

            // (b) No en-US leak. The transform appends a combining accent to every letter, so the
            // probe's plain-ASCII form can only appear if the string bypassed the resource pipeline.
            var probed = await ProbedTextAsync().ConfigureAwait(false);
            probed.Should().NotContain(
                scanned.EnglishProbe,
                $"{scanned.Path} must render '{scanned.EnglishProbe}' through the localizer, not hard-coded");

            // (c) No horizontal page overflow under the roughly 40% expansion. Grids scroll inside
            // their own containers; the DOCUMENT must not scroll sideways.
            var overflow = await Page.EvaluateAsync<int>(
                "() => Math.max(0, document.scrollingElement.scrollWidth - document.scrollingElement.clientWidth)").ConfigureAwait(false);
            overflow.Should().BeLessThanOrEqualTo(
                OverflowTolerancePx,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{scanned.Path} overflows horizontally by {overflow}px under the {PseudoLocale} text expansion; the layout must tolerate longer translations"));
        }
    }

    [Fact]
    public async Task DefaultCulture_DoesNotLeakPseudoSentinel()
    {
        foreach (var scanned in ScannedPages)
        {
            // Fresh browser context per test (E2ETestBase), so no culture cookie is present and the
            // default culture renders.
            await Page.GotoAndWaitForBlazorAsync(scanned.Path).ConfigureAwait(false);
            await SettleAsync(scanned.SettleSelector).ConfigureAwait(false);

            var content = await Page.ContentAsync().ConfigureAwait(false);
            content.Should().NotContain(
                Sentinel,
                $"{scanned.Path} must never ship pseudo-localized text to a real locale");

            if (AssertProbePresentUnderDefaultCulture)
            {
                var probed = await ProbedTextAsync().ConfigureAwait(false);
                probed.Should().Contain(
                    scanned.EnglishProbe,
                    $"the probe for {scanned.Path} must still match its resource, or the pseudo leak check passes vacuously");
            }
        }
    }

    /// <summary>
    /// The text the en-US probe is searched in: the whole document by default, or the body's visible
    /// text when <see cref="ProbeVisibleTextOnly"/> is set.
    /// </summary>
    /// <returns>The text to probe.</returns>
    private async Task<string> ProbedTextAsync() =>
        ProbeVisibleTextOnly
            ? await Page.InnerTextAsync("body").ConfigureAwait(false)
            : await Page.ContentAsync().ConfigureAwait(false);

    /// <summary>
    /// Waits for the page's async content and for any loading indicator to clear, so the
    /// sentinel/leak/overflow checks measure the settled DOM.
    /// </summary>
    /// <param name="settleSelector">The page's settle selector, or null.</param>
    private async Task SettleAsync(string? settleSelector)
    {
        if (settleSelector is not null)
        {
            await Expect(Page.Locator(settleSelector).First)
                .ToBeVisibleAsync(new() { Timeout = Timeout }).ConfigureAwait(false);
        }

        await Expect(Page.Locator("[role='progressbar']"))
            .ToHaveCountAsync(0, new() { Timeout = Timeout }).ConfigureAwait(false);
    }
}
