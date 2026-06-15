using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests;

public sealed class LoginPageE2ETests : GalleryAxeTestBase
{
    public LoginPageE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task LoginPage_Renders_KeyElements()
    {
        var loginPage = new LoginPage(Page);
        await loginPage.GotoAsync();

        await Expect(loginPage.EmailField).ToBeVisibleAsync();
        await Expect(loginPage.PasswordField).ToBeVisibleAsync();
        await Expect(loginPage.LoginButton).ToBeVisibleAsync();
        await Expect(loginPage.CreateAccountLink).ToBeVisibleAsync();
    }

    [Fact]
    public async Task LoginPage_HasNoWcag21AaViolations()
    {
        var loginPage = new LoginPage(Page);
        await loginPage.GotoAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task FillAndVerify_PersistsValueThroughBlazorHydration()
    {
        // Exercises the shipped MMCA.Common.Testing.E2E fill helper end-to-end against a real,
        // re-hydrating MudTextField — the auto-waiting ToHaveValueAsync replacement for the old
        // fixed Task.Delay retry loops. The value must still be present after hydration settles.
        var loginPage = new LoginPage(Page);
        await loginPage.GotoAsync();

        await loginPage.EmailField.FillAndVerifyAsync("e2e@example.com");
        await loginPage.PasswordField.FillAndVerifyAsync("S3cret-Pass!");

        await Expect(loginPage.EmailField).ToHaveValueAsync("e2e@example.com");
        await Expect(loginPage.PasswordField).ToHaveValueAsync("S3cret-Pass!");
    }
}
