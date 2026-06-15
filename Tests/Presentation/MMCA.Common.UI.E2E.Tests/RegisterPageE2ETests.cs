using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests;

public sealed class RegisterPageE2ETests : GalleryAxeTestBase
{
    public RegisterPageE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task RegisterPage_Renders_KeyElements()
    {
        var registerPage = new RegisterPage(Page);
        await registerPage.GotoAsync();

        await Expect(registerPage.FirstNameField).ToBeVisibleAsync();
        await Expect(registerPage.LastNameField).ToBeVisibleAsync();
        await Expect(registerPage.EmailField).ToBeVisibleAsync();
        await Expect(registerPage.PasswordField).ToBeVisibleAsync();
        await Expect(registerPage.ConfirmPasswordField).ToBeVisibleAsync();
        await Expect(registerPage.RegisterButton).ToBeVisibleAsync();
    }

    [Fact]
    public async Task RegisterPage_HasNoWcag21AaViolations()
    {
        var registerPage = new RegisterPage(Page);
        await registerPage.GotoAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }
}
