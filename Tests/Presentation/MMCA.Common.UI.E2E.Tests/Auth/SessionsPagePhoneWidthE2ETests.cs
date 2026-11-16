using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Auth;

/// <summary>
/// ADC manual test Run 17, A-54: in Spanish at 320px the signed-in devices table and the sign-in
/// history table stacked into label/value rows wider than their card (the sign-in history row measured
/// 332px in a 288px card), so each scrolled sideways inside the card. The <c>.mmca-auth-table</c> rules
/// in app.css let a stacked row wrap. Document-level overflow (<see cref="PseudoLocalizationE2ETests"/>)
/// cannot see this, because the table container clips and scrolls its own content.
/// </summary>
public sealed class SessionsPagePhoneWidthE2ETests : GalleryAxeTestBase
{
    private const int PhoneWidth = 320;
    private const int PhoneHeight = 800;
    private const string Spanish = "es";

    // Leading words of Auth.Sessions.TableLabel and Auth.SignIns.TableLabel in SharedResource.es.resx
    // (a substring match, so the accented tail stays out of this ASCII source).
    private static readonly string[] SpanishTableNames = ["Dispositivos con la", "Historial de inicios"];

    public SessionsPagePhoneWidthE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task Sessions_InSpanishAt320px_TheAccountTablesDoNotScrollSideways()
    {
        await SeedSignedInSpanishCookiesAsync();
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight);

        await Page.GotoAndWaitForBlazorAsync("/profile/sessions");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Located by their Spanish region names, which also proves the page rendered in Spanish.
        foreach (var tableName in SpanishTableNames)
        {
            var table = Page.GetByRole(AriaRole.Region, new() { Name = tableName });
            await Expect(table).ToBeVisibleAsync();

            var overflow = await SidewaysScrollPixelsAsync(table);
            Assert.True(overflow <= 1,
                string.Create(CultureInfo.InvariantCulture,
                    $"the '{tableName}' table scrolls sideways by {overflow}px in Spanish at {PhoneWidth}px; its stacked rows must wrap inside the card (ADC Run 17, A-54)"));
        }
    }

    // The widest sideways scroll in the table wrapper or its MudBlazor scroll container
    // (`.mud-table-container`, which app.css gives overflow-x: auto below 1024px).
    private static Task<int> SidewaysScrollPixelsAsync(ILocator table) =>
        table.EvaluateAsync<int>(
            "wrapper => Math.max(0, ...[wrapper, ...wrapper.querySelectorAll('.mud-table-container')].map(e => e.scrollWidth - e.clientWidth))");

    // The page carries a real [Authorize]; the gallery's cookie-toggled fake scheme signs it in (as in
    // SessionsPageE2ETests). The culture cookie localizes both the prerender and the interactive circuit
    // (as in PseudoLocalizationE2ETests).
    private Task SeedSignedInSpanishCookiesAsync() =>
        Page.Context.AddCookiesAsync(
        [
            new Cookie
            {
                Name = "gallery_auth",
                Value = "1",
                Url = BaseUrl,
            },
            new Cookie
            {
                Name = CookieRequestCultureProvider.DefaultCookieName,
                Value = Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(Spanish))),
                Url = BaseUrl,
            },
        ]);
}
