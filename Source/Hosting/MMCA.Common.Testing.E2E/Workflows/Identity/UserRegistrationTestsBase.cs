using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.Testing.E2E.Workflows.Identity;

public abstract class UserRegistrationTestsBase : E2ETestBase
{
    protected UserRegistrationTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Register_WithValidData_ShouldNavigateToHomePage()
    {
        // Arrange
        var registerPage = new RegisterPage(Page);
        var id = UniqueId();
        var email = $"reg-{id}@test.com";

        // Act
        await registerPage.GotoAsync();
        await registerPage.RegisterAsync($"First{id}", $"Last{id}", email, "TestPass123!");

        // Assert — should navigate away from register page after success
        await Expect(Page).Not.ToHaveURLAsync(new Regex("/register$"));

        // Verify user is logged in by checking for Logout button in the app bar
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Register_WithMismatchedPasswords_ShouldShowError()
    {
        // Arrange
        var registerPage = new RegisterPage(Page);
        var id = UniqueId();

        // Act
        await registerPage.GotoAsync();
        await registerPage.FirstNameField.FillAsync($"First{id}");
        await registerPage.LastNameField.FillAsync($"Last{id}");
        await registerPage.EmailField.FillAsync($"mismatch-{id}@test.com");
        await registerPage.PasswordField.FillAsync("TestPass123!");
        await registerPage.ConfirmPasswordField.FillAsync("DifferentPass123!");
        await registerPage.RegisterButton.ClickAsync();

        // Assert — should show password mismatch error and stay on register page
        await Expect(registerPage.ErrorAlert).ToBeVisibleAsync(new() { Timeout = 10_000 });
        await Expect(Page).ToHaveURLAsync(new Regex("/register$"));
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ShouldShowError()
    {
        // Arrange — first register a user
        var (email, _) = await RegisterNewUserAsync();

        // Act — try to register again with the same email (navigate back to register page)
        var registerPage = new RegisterPage(Page);
        await registerPage.GotoAsync();
        await registerPage.RegisterAsync("Dup", "User", email, "TestPass123!");

        // Assert — should show error about email already in use
        await Page.WaitForLoadStateAsync(LoadState.Load);
        await Expect(registerPage.ErrorAlert).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }
}
