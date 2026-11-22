using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Auth;

/// <summary>
/// The sign-in page's new states in a real browser (design 5.5), against the gallery's canned sign-in:
/// <c>two-factor@gallery.test</c> is answered "second factor required" (and <c>000000</c> "invalid"),
/// <c>unconfirmed@gallery.test</c> "email not confirmed". The code step appears with focus on the code
/// field, a wrong code shows the alert, the resend prompt appears and reaches its sent status, and each
/// state passes the axe-core WCAG 2.1 AA scan.
/// </summary>
public sealed class LoginSecondFactorE2ETests : GalleryAxeTestBase
{
    private const string TwoFactorEmail = "two-factor@gallery.test";
    private const string UnconfirmedEmail = "unconfirmed@gallery.test";
    private const string Password = "S3cret-Pass!";

    public LoginSecondFactorE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task TheCodeStep_AppearsAfterTheCredentials_WithFocusOnTheCodeField()
    {
        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(Page.GetByTestId("login-second-factor-code")).ToHaveCountAsync(0);

        await login.LoginAsync(TwoFactorEmail, Password);

        await Expect(Page.GetByTestId("login-second-factor-prompt")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("login-second-factor-code")).ToBeFocusedAsync();
        await Expect(login.EmailField).Not.ToBeEditableAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Verify and sign in" })).ToBeVisibleAsync();
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task AWrongCode_ShowsTheAlert_AndKeepsTheStep()
    {
        var login = new LoginPage(Page);
        await login.GotoAsync();
        await login.LoginAsync(TwoFactorEmail, Password);
        var code = Page.GetByTestId("login-second-factor-code");
        await Expect(code).ToBeFocusedAsync();

        await code.FillAndVerifyAsync("000000");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Verify and sign in" }).ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "That code did not work." })).ToBeVisibleAsync();
        await Expect(code).ToBeVisibleAsync();
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task StartOver_LeavesTheStep()
    {
        var login = new LoginPage(Page);
        await login.GotoAsync();
        await login.LoginAsync(TwoFactorEmail, Password);
        await Expect(Page.GetByTestId("login-second-factor-code")).ToBeVisibleAsync();

        await Page.GetByTestId("login-second-factor-start-over").ClickAsync();

        await Expect(Page.GetByTestId("login-second-factor-code")).ToHaveCountAsync(0);
        await Expect(login.EmailField).ToBeEditableAsync();
        await Expect(login.PasswordField).ToHaveValueAsync(string.Empty);
    }

    [Fact]
    public async Task AnUnconfirmedAddress_ShowsTheResendPrompt_AndItsSentStatus()
    {
        var login = new LoginPage(Page);
        await login.GotoAsync();

        await login.LoginAsync(UnconfirmedEmail, Password);

        var prompt = Page.GetByTestId("email-confirmation-resend-prompt");
        await Expect(prompt).ToBeVisibleAsync();
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);

        var resend = Page.GetByTestId("email-confirmation-resend");
        await resend.ClickAsync();

        await Expect(Page.GetByTestId("email-confirmation-resend-sent")).ToBeVisibleAsync();
        await Expect(resend).ToBeDisabledAsync();
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task AnyOtherRefusal_ShowsNoResendPrompt()
    {
        var login = new LoginPage(Page);
        await login.GotoAsync();

        await login.LoginAsync("someone@gallery.test", Password);

        await Expect(login.ErrorAlert).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("email-confirmation-resend-prompt")).ToHaveCountAsync(0);
    }
}
