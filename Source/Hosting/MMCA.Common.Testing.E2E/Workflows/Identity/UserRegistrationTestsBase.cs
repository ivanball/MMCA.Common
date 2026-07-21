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
        await registerPage.GotoAsync().ConfigureAwait(false);
        await registerPage.RegisterAsync($"First{id}", $"Last{id}", email, "TestPass123!").ConfigureAwait(false);

        // Assert — should navigate away from register page after success
        await Expect(Page).Not.ToHaveURLAsync(new Regex("/register$")).ConfigureAwait(false);

        // Verify user is logged in by checking for Logout button in the app bar
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" })).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task Register_WithMismatchedPasswords_ShouldShowError()
    {
        // Arrange
        var registerPage = new RegisterPage(Page);
        var id = UniqueId();

        // Act — fill with the re-hydration-safe helper so every value is bound to the EditForm model
        // before submit (under WebAssembly a plain Fill on a not-yet-hydrated input can be wiped). Submit
        // ONCE: the [Compare] validation fires OnInvalidSubmit and the error alert stays visible, so a
        // re-clicking ClickAndVerify is wrong here (each re-submit re-runs validation and makes the alert
        // flicker out from under the wait); a single click plus an auto-waiting visibility assert is right.
        await registerPage.GotoAsync().ConfigureAwait(false);
        await registerPage.FirstNameField.FillAndVerifyAsync($"First{id}").ConfigureAwait(false);
        await registerPage.LastNameField.FillAndVerifyAsync($"Last{id}").ConfigureAwait(false);
        await registerPage.EmailField.FillAndVerifyAsync($"mismatch-{id}@test.com").ConfigureAwait(false);
        await registerPage.PasswordField.FillAndVerifyAsync("TestPass123!").ConfigureAwait(false);
        await registerPage.ConfirmPasswordField.FillAndVerifyAsync("DifferentPass123!").ConfigureAwait(false);
        await registerPage.RegisterButton.ClickAsync().ConfigureAwait(false);

        // Assert — the [Compare] mismatch is caught CLIENT-side, so it surfaces as the field-level
        // validation message rather than a page-level error alert. (The Server-mode prerender path
        // produced a ".mud-alert-text-error" alert; the WebAssembly path shows only the inline field
        // error, with no server round-trip.) Assert the validation TEXT, which is present in both render
        // modes, and that we stay on /register.
        await Expect(Page.GetByText("Passwords do not match")).ToBeVisibleAsync(new() { Timeout = 10_000 }).ConfigureAwait(false);
        await Expect(Page).ToHaveURLAsync(new Regex("/register$")).ConfigureAwait(false);
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ShouldShowError()
    {
        // Arrange — first register a user
        var (email, _) = await RegisterNewUserAsync().ConfigureAwait(false);

        // Act — try to register again with the same email (navigate back to register page)
        var registerPage = new RegisterPage(Page);
        await registerPage.GotoAsync().ConfigureAwait(false);
        await registerPage.RegisterAsync("Dup", "User", email, "TestPass123!").ConfigureAwait(false);

        // Assert — should show error about email already in use
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
        await Expect(registerPage.ErrorAlert).ToBeVisibleAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);
    }

    [Fact]
    public async Task RegisterPage_ShouldHaveNoAccessibilityViolations()
    {
        // Arrange
        var registerPage = new RegisterPage(Page);
        await registerPage.GotoAsync().ConfigureAwait(false);

        // Assert — axe-core finds zero WCAG 2.1 AA violations on the registration page. Scoped to the
        // documented WCAG 2.1 AA target (AxeOptions.Wcag21Aa); axe "best-practice" advisories are intentionally
        // out of scope so this gate fails only on real conformance violations — matching the gallery + consumer scans.
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa).ConfigureAwait(false);
    }
}
