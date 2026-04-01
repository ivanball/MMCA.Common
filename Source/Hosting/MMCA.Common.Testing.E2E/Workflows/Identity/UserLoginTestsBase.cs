using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.Testing.E2E.Workflows.Identity;

public abstract class UserLoginTestsBase : E2ETestBase
{
    protected UserLoginTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Login_WithValidCredentials_ShouldNavigateToHomePage()
    {
        // Arrange — register a fresh user
        var (email, password) = await RegisterNewUserAsync();

        // Log out first (registration auto-logs in) — logout does a forceLoad redirect
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" }).ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);

        // Act — log in with the registered credentials
        await LoginAsync(email, password);

        // Assert — should be on the home page, not the login page
        await Expect(Page).Not.ToHaveURLAsync(new Regex("/login$"));

        // Logout button should be visible (user is authenticated)
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" })).ToBeVisibleAsync();

        // Login/Register links should NOT be visible (user is authenticated)
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Sign In" })).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task Login_WithInvalidPassword_ShouldShowError()
    {
        // Arrange
        var loginPage = new LoginPage(Page);
        await loginPage.GotoAsync();

        // Act
        await loginPage.LoginAsync("nonexistent@test.com", "WrongPassword!");

        // Assert — wait for the error to appear after the async login call completes
        await Page.WaitForLoadStateAsync(LoadState.Load);
        await Expect(loginPage.ErrorAlert).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(Page).ToHaveURLAsync(new Regex("/login"));
    }

    [Fact]
    public async Task Login_NavigateToCreateAccount_ShouldGoToRegisterPage()
    {
        // Arrange
        var loginPage = new LoginPage(Page);
        await loginPage.GotoAsync();

        // Act
        await loginPage.CreateAccountLink.ClickAsync();

        // Assert
        await Expect(Page).ToHaveURLAsync(new Regex("/register$"));
    }
}
