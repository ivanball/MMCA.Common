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
        var (email, password) = await RegisterNewUserAsync().ConfigureAwait(false);

        // Log out first (registration auto-logs in) — logout does a forceLoad redirect to /login.
        // Wait for that URL, not for LoadState.Load: the CURRENT document's load event fired long ago,
        // so WaitForLoadState returns immediately and LoginAsync would race the in-flight logout
        // navigation (its pre-login cleanup evaluate dies with "execution context was destroyed").
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" }).ClickAsync().ConfigureAwait(false);
        await Page.WaitForURLAsync(new Regex("/login"), new() { Timeout = 15_000 }).ConfigureAwait(false);

        // Act — log in with the registered credentials
        await LoginAsync(email, password).ConfigureAwait(false);

        // Assert — should be on the home page, not the login page
        await Expect(Page).Not.ToHaveURLAsync(new Regex("/login$")).ConfigureAwait(false);

        // Logout button should be visible (user is authenticated)
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" })).ToBeVisibleAsync().ConfigureAwait(false);

        // Login/Register links should NOT be visible (user is authenticated)
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Sign In" })).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task Login_WithInvalidPassword_ShouldShowError()
    {
        // Arrange
        var loginPage = new LoginPage(Page);
        await loginPage.GotoAsync().ConfigureAwait(false);

        // Act
        await loginPage.LoginAsync("nonexistent@test.com", "WrongPassword!").ConfigureAwait(false);

        // Assert — wait for the error to appear after the async login call completes
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
        await Expect(loginPage.ErrorAlert).ToBeVisibleAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);
        await Expect(Page).ToHaveURLAsync(new Regex("/login")).ConfigureAwait(false);
    }

    [Fact]
    public async Task Login_NavigateToCreateAccount_ShouldGoToRegisterPage()
    {
        // Arrange
        var loginPage = new LoginPage(Page);
        await loginPage.GotoAsync().ConfigureAwait(false);

        // Act
        await loginPage.CreateAccountLink.ClickAsync().ConfigureAwait(false);

        // Assert
        await Expect(Page).ToHaveURLAsync(new Regex("/register$")).ConfigureAwait(false);
    }

    [Fact]
    public async Task LoginPage_ShouldHaveNoAccessibilityViolations()
    {
        // Arrange
        var loginPage = new LoginPage(Page);
        await loginPage.GotoAsync().ConfigureAwait(false);

        // Assert — axe-core finds zero WCAG 2.1 AA violations on the login page. Scoped to the documented
        // WCAG 2.1 AA target (AxeOptions.Wcag21Aa); axe "best-practice" advisories are intentionally out of
        // scope so this gate fails only on real conformance violations — matching the gallery + consumer scans.
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa).ConfigureAwait(false);
    }
}
