using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Auth;

/// <summary>
/// Render-smoke + WCAG 2.1 AA accessibility scan for the shared signed-in devices page, rendered
/// against the gallery's canned session list (one current device, one other).
/// </summary>
public sealed class SessionsPageE2ETests : GalleryAxeTestBase
{
    public SessionsPageE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task Sessions_Renders_AndHasNoWcag21AaViolations()
    {
        await SeedSignedInCookieAsync();
        await Page.GotoAndWaitForBlazorAsync("/profile/sessions");

        // The populated table, its current-device marker, and a live per-device sign-out button:
        // the three surfaces the scan exists to cover.
        await Expect(Page.GetByText("Chrome on Windows")).ToBeVisibleAsync();
        // Exact: the revoke-all hint below the table also contains the words "this device".
        await Expect(Page.GetByText("This device", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of Safari on iOS" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of every device, including this one" })).ToBeVisibleAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    // The page carries a real [Authorize] (rubric section 25); the gallery's cookie-toggled fake
    // scheme (GalleryFakeAuthenticationHandler) signs this scan in so the guarded page renders.
    private async Task SeedSignedInCookieAsync() =>
        await Page.Context.AddCookiesAsync(
        [
            new Cookie
            {
                Name = "gallery_auth",
                Value = "1",
                Url = BaseUrl,
            },
        ]);
}
