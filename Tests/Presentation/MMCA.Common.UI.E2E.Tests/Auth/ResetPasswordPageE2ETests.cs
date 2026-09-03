using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Auth;

public sealed class ResetPasswordPageE2ETests : GalleryAxeTestBase
{
    public ResetPasswordPageE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task ResetPasswordPage_Renders_KeyElements()
    {
        var resetPage = new ResetPasswordPage(Page);
        await resetPage.GotoAsync();

        await Expect(resetPage.EmailField).ToBeVisibleAsync();
        await Expect(resetPage.TokenField).ToBeVisibleAsync();
        await Expect(resetPage.NewPasswordField).ToBeVisibleAsync();
        await Expect(resetPage.ConfirmPasswordField).ToBeVisibleAsync();
        await Expect(resetPage.SubmitButton).ToBeVisibleAsync();
        await Expect(resetPage.GoToLoginLink).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ResetPasswordPage_WithLinkQueryString_PrefillsEmailAndToken()
    {
        // The emailed link carries both values; the fields stay editable so the raw token from the same
        // email can also be typed in by hand.
        var resetPage = new ResetPasswordPage(Page);
        await resetPage.GotoWithLinkAsync("linked@example.com", "abc-123_token");

        await Expect(resetPage.EmailField).ToHaveValueAsync("linked@example.com");
        await Expect(resetPage.TokenField).ToHaveValueAsync("abc-123_token");
    }

    [Fact]
    public async Task ResetPasswordPage_HasNoWcag21AaViolations()
    {
        var resetPage = new ResetPasswordPage(Page);
        await resetPage.GotoAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }
}
