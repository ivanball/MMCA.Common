using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.Testing.E2E.Workflows.Identity;

/// <summary>
/// Password-recovery workflow coverage for the shared <c>/forgot-password</c> and
/// <c>/reset-password</c> pages. The real token round-trip is deliberately NOT exercised here: the
/// token only reaches the user by email, so consuming one is an app-side integration-test concern.
/// What E2E owns is the reachability of the flow, the anti-enumeration confirmation, client-side
/// validation, and WCAG 2.1 AA conformance of both pages.
/// </summary>
public abstract class PasswordResetTestsBase : E2ETestBase
{
    protected PasswordResetTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task LoginPage_ForgotPasswordLink_NavigatesToForgotPasswordPage()
    {
        // Arrange
        var loginPage = new LoginPage(Page);
        await loginPage.GotoAsync().ConfigureAwait(false);

        var forgotLink = Page.GetByRole(AriaRole.Link, new() { Name = "Forgot your password?" });

        // Assert the affordance exists at all: a user locked out of their account has no other entry point.
        await Expect(forgotLink).ToBeVisibleAsync().ConfigureAwait(false);

        // Act
        await forgotLink.ClickAsync().ConfigureAwait(false);

        // Assert
        await Expect(Page).ToHaveURLAsync(new Regex("/forgot-password$")).ConfigureAwait(false);
    }

    [Fact]
    public async Task ForgotPassword_WithUnknownEmail_ShowsTheSameConfirmation()
    {
        // Arrange: an address that certainly has no account
        var forgotPage = new ForgotPasswordPage(Page);
        await forgotPage.GotoAsync().ConfigureAwait(false);

        // Act
        await forgotPage.RequestResetAsync($"unknown-{UniqueId()}@test.com").ConfigureAwait(false);

        // Assert: the confirmation state is unconditional (anti-enumeration): an unknown address must
        // look exactly like a known one, so no error alert and no navigation away from the page.
        await Expect(forgotPage.ConfirmationAlert).ToBeVisibleAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);
        await Expect(Page).ToHaveURLAsync(new Regex("/forgot-password$")).ConfigureAwait(false);
        await Expect(forgotPage.BackToLoginLink).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task ResetPassword_WithEmptyForm_ShowsClientValidationErrors()
    {
        // Arrange
        var resetPage = new ResetPasswordPage(Page);
        await resetPage.GotoAsync().ConfigureAwait(false);

        // Act: submit with nothing filled in. DataAnnotations block OnValidSubmit, so nothing is sent.
        await resetPage.SubmitButton.ClickAsync().ConfigureAwait(false);

        // Assert: the field-level messages are present in both render modes (Server prerender and
        // WebAssembly), unlike a page-level alert, so assert the validation TEXT. Same reasoning as
        // the mismatched-password registration test.
        await Expect(Page.GetByText("Email is required")).ToBeVisibleAsync(new() { Timeout = 10_000 }).ConfigureAwait(false);
        await Expect(Page.GetByText("Reset token is required")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page).ToHaveURLAsync(new Regex("/reset-password$")).ConfigureAwait(false);
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldHaveNoAccessibilityViolations()
    {
        // Arrange
        var forgotPage = new ForgotPasswordPage(Page);
        await forgotPage.GotoAsync().ConfigureAwait(false);

        // Assert: axe-core finds zero WCAG 2.1 AA violations. Scoped to the documented WCAG 2.1 AA
        // target (AxeOptions.Wcag21Aa); axe "best-practice" advisories are intentionally out of scope so
        // this gate fails only on real conformance violations, matching the other Identity workflows.
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa).ConfigureAwait(false);
    }

    [Fact]
    public async Task ResetPasswordPage_ShouldHaveNoAccessibilityViolations()
    {
        // Arrange
        var resetPage = new ResetPasswordPage(Page);
        await resetPage.GotoAsync().ConfigureAwait(false);

        // Assert: same WCAG 2.1 AA scope as above.
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa).ConfigureAwait(false);
    }
}
