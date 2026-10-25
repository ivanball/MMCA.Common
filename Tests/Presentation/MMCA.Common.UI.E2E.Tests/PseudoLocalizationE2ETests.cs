using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Playwright;
using MMCA.Common.Shared.Globalization;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests;

/// <summary>
/// Pseudo-localization CI gate (ADR-027 §8 / rubric §27). Renders the real shared pages under the
/// <c>qps-Ploc</c> pseudo-locale (activated via the query-string request-culture provider, which the
/// gallery host enables unconditionally as test-only infrastructure) and asserts two things per page:
/// <list type="number">
/// <item><description>the <c>[!!</c> bracket sentinel renders, proving every displayed string made the
/// full resource round-trip (resx -> IStringLocalizer -> PseudoStringLocalizer -> markup) rather than
/// being hard-coded;</description></item>
/// <item><description>the page does not overflow horizontally under the pseudo pass's ~40% text
/// expansion — the rubric's layout-tolerance criterion, previously unevidenced.</description></item>
/// </list>
/// Running in every leg of the required <c>ui-e2e</c> matrix (chromium, firefox and webkit) promotes the pseudo pass from a developer
/// diagnostic to a CI gate. A leak-guard test also asserts the sentinel is absent under the default
/// culture, so pseudo text can never ship to a real locale unnoticed.
/// </summary>
public sealed class PseudoLocalizationE2ETests : GalleryAxeTestBase
{
    public PseudoLocalizationE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    public static TheoryData<string> ScannedPaths => new("/login", "/register", "/forgot-password", "/reset-password", "/components");

    /// <summary>
    /// The culture reaches BOTH halves of an interactive page. The query string localizes only the
    /// prerender: the interactive Server circuit opens on <c>/_blazor</c>, which carries no query
    /// string, so without the culture cookie the circuit re-rendered the page in the default culture
    /// and the <c>[!!</c> sentinel vanished. Whether <c>ContentAsync</c> read before or after that
    /// swap was a race webkit usually lost (the "flake" behind the 2026-07 and 2026-10-25 reds; a
    /// reload-and-retry could not help, since every reload repeats the race). The cookie is how a real
    /// app carries the culture (the culture switcher writes it), and waiting for the network to go
    /// idle makes the assertion read the interactive render, not whichever half arrived first.
    /// </summary>
    [Theory]
    [MemberData(nameof(ScannedPaths))]
    public async Task PseudoLocale_RendersSentinel_AndDoesNotOverflowHorizontally(string path)
    {
        await Page.Context.AddCookiesAsync(
        [
            new Cookie
            {
                Name = CookieRequestCultureProvider.DefaultCookieName,
                Value = Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(SupportedCultures.PseudoLocale))),
                Url = BaseUrl,
            },
        ]);

        await Page.GotoAsync(WithPseudoCulture(path));
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var content = await Page.ContentAsync();
        Assert.Contains("[!!", content, StringComparison.Ordinal);

        var overflow = await HorizontalOverflowPixelsAsync();
        Assert.True(overflow <= 1,
            string.Create(CultureInfo.InvariantCulture,
                $"{path} overflows horizontally by {overflow}px under the qps-Ploc ~40% text expansion; the layout must tolerate longer translations (rubric §27 layout tolerance)"));
    }

    [Fact]
    public async Task DefaultCulture_DoesNotLeakPseudoSentinel()
    {
        await Page.GotoAsync("/login");
        await Page.WaitForLoadStateAsync();

        var content = await Page.ContentAsync();
        Assert.DoesNotContain("[!!", content, StringComparison.Ordinal);
    }

    private static string WithPseudoCulture(string path) =>
        $"{path}?culture={SupportedCultures.PseudoLocale}&ui-culture={SupportedCultures.PseudoLocale}";

    private Task<int> HorizontalOverflowPixelsAsync() =>
        Page.EvaluateAsync<int>(
            "() => Math.max(0, document.scrollingElement.scrollWidth - document.scrollingElement.clientWidth)");
}
