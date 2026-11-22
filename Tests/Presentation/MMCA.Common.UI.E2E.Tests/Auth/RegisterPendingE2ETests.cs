using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Auth;

/// <summary>
/// The Register page behind the email gate in a real browser (design 5.5): registering the gallery's
/// <c>pending@gallery.test</c> is answered "confirmation pending", so the page shows the check-your-email
/// heading with the resend button, the URL stays <c>/register</c>, the state passes the axe-core
/// WCAG 2.1 AA scan, and "Go to sign in" opens sign-in with the address filled in.
/// </summary>
public sealed class RegisterPendingE2ETests : GalleryAxeTestBase
{
    private const string PendingEmail = "pending@gallery.test";

    public RegisterPendingE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task APendingRegistration_ShowsCheckYourEmail_AndStaysOnRegister()
    {
        var register = new RegisterPage(Page);
        await register.GotoAsync();

        await register.RegisterAsync("Ada", "Lovelace", PendingEmail, "Str0ng!Passw0rd");

        var heading = Page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" });
        await Expect(heading).ToBeVisibleAsync();
        await Expect(heading).ToBeFocusedAsync();
        await Expect(Page.GetByTestId("register-pending")).ToContainTextAsync(PendingEmail);
        await Expect(Page.GetByTestId("email-confirmation-resend")).ToBeVisibleAsync();
        await Expect(Page).ToHaveURLAsync($"{BaseUrl}/register");
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task GoToSignIn_OpensSignInWithTheAddressFilledIn()
    {
        var register = new RegisterPage(Page);
        await register.GotoAsync();
        await register.RegisterAsync("Ada", "Lovelace", PendingEmail, "Str0ng!Passw0rd");
        await Expect(Page.GetByTestId("register-pending")).ToBeVisibleAsync();

        await Page.GetByTestId("register-pending-go-to-sign-in").ClickAsync();

        await Expect(Page).ToHaveURLAsync($"{BaseUrl}/login");
        await Expect(new LoginPage(Page).EmailField).ToHaveValueAsync(PendingEmail);
    }
}
