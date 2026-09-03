using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Auth;

public sealed class ForgotPasswordPageE2ETests : GalleryAxeTestBase
{
    public ForgotPasswordPageE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task ForgotPasswordPage_Renders_KeyElements()
    {
        var forgotPage = new ForgotPasswordPage(Page);
        await forgotPage.GotoAsync();

        await Expect(forgotPage.EmailField).ToBeVisibleAsync();
        await Expect(forgotPage.SubmitButton).ToBeVisibleAsync();
        await Expect(forgotPage.BackToLoginLink).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ForgotPasswordPage_AfterSubmit_ShowsConfirmationEvenWithoutABackend()
    {
        // The gallery's stub auth service always answers "not accepted", which is exactly the case the
        // anti-enumeration rule covers: the confirmation must appear anyway.
        var forgotPage = new ForgotPasswordPage(Page);
        await forgotPage.GotoAsync();

        await forgotPage.RequestResetAsync("nobody@example.com");

        await Expect(forgotPage.ConfirmationAlert).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ForgotPasswordPage_HasNoWcag21AaViolations()
    {
        var forgotPage = new ForgotPasswordPage(Page);
        await forgotPage.GotoAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task ForgotPasswordPage_ConfirmationState_HasNoWcag21AaViolations()
    {
        // The confirmation replaces the form, so it is a distinct rendering that the form-state scan
        // above never reaches.
        var forgotPage = new ForgotPasswordPage(Page);
        await forgotPage.GotoAsync();

        await forgotPage.RequestResetAsync("nobody@example.com");
        await Expect(forgotPage.ConfirmationAlert).ToBeVisibleAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }
}
