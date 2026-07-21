using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.Testing.E2E.Workflows.Identity;

public abstract class ProfileManagementTestsBase : E2ETestBase
{
    protected ProfileManagementTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Whether the app's profile page offers an email-change section. Off by default: no reference app
    /// currently exposes one, and the previous DOM-probing version of the test passed vacuously when the
    /// field was absent (reporting coverage for a journey the app does not offer). A consumer that ships
    /// email change opts in by overriding to true; the test then FAILS LOUD if the field goes missing.
    /// </summary>
    protected virtual bool ProfileSupportsEmailChange => false;

    [Fact]
    public async Task ChangeName_ShouldUpdateProfileName()
    {
        // Arrange
        var (_, _) = await RegisterNewUserAsync().ConfigureAwait(false);
        var profilePage = new ProfilePage(Page);
        await profilePage.GotoAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);

        var newFirstName = $"NewFirst{UniqueId()[..4]}";
        var newLastName = $"NewLast{UniqueId()[..4]}";

        // Act
        await profilePage.FirstNameField.ClearAsync().ConfigureAwait(false);
        await profilePage.FirstNameField.FillAsync(newFirstName).ConfigureAwait(false);
        await profilePage.LastNameField.ClearAsync().ConfigureAwait(false);
        await profilePage.LastNameField.FillAsync(newLastName).ConfigureAwait(false);
        await profilePage.SaveNameButton.ClickAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);

        // Assert — reload profile and verify name persisted
        await profilePage.GotoAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
        var firstName = await profilePage.FirstNameField.InputValueAsync().ConfigureAwait(false);
        var lastName = await profilePage.LastNameField.InputValueAsync().ConfigureAwait(false);
        firstName.Should().Be(newFirstName);
        lastName.Should().Be(newLastName);
    }

    [Fact]
    public async Task ChangeAddress_ShouldUpdateProfileAddress()
    {
        // Arrange
        await RegisterNewUserAsync().ConfigureAwait(false);
        var profilePage = new ProfilePage(Page);
        await profilePage.GotoAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);

        // Act
        await profilePage.AddressLine1Field.FillAsync("123 Test Street").ConfigureAwait(false);
        await profilePage.CityField.FillAsync("TestCity").ConfigureAwait(false);
        await profilePage.StateField.FillAsync("TS").ConfigureAwait(false);
        await profilePage.ZipCodeField.FillAsync("12345").ConfigureAwait(false);
        await profilePage.CountryField.FillAsync("TestCountry").ConfigureAwait(false);
        await profilePage.SaveAddressButton.ClickAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);

        // Assert — reload and verify
        await profilePage.GotoAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
        var address = await profilePage.AddressLine1Field.InputValueAsync().ConfigureAwait(false);
        address.Should().Be("123 Test Street");
    }

    [Fact]
    public async Task ChangePassword_WithValidCurrentPassword_ShouldSucceed()
    {
        // Arrange — register with a known password
        var (email, password) = await RegisterNewUserAsync().ConfigureAwait(false);
        var profilePage = new ProfilePage(Page);
        await profilePage.GotoAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);

        var newPassword = "NewTestPass456!";

        // Act
        await FillFieldAsync(profilePage.CurrentPasswordField, password).ConfigureAwait(false);
        await FillFieldAsync(profilePage.NewPasswordField, newPassword).ConfigureAwait(false);
        await FillFieldAsync(profilePage.ConfirmNewPasswordField, newPassword).ConfigureAwait(false);
        await profilePage.ChangePasswordButton.ClickAsync().ConfigureAwait(false);

        // Wait for the password change API call to complete (snackbar confirms success)
        await Expect(Page.GetByText("Password changed successfully.")).ToBeVisibleAsync(new() { Timeout = 30_000 }).ConfigureAwait(false);

        // Assert — log out and log back in with new password. Wait for the logout forceLoad's /login
        // URL, not LoadState.Load: the CURRENT document's load event fired long ago, so that wait
        // returns immediately and LoginAsync races the in-flight logout navigation (its /login goto
        // dies with ERR_ABORTED / "interrupted by another navigation"). Same fix as
        // UserLoginTestsBase.Login_WithValidCredentials (v1.103.1); this was the one remaining
        // sign-out-then-login site still on the racy pattern.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" }).ClickAsync().ConfigureAwait(false);
        await Page.WaitForURLAsync(new Regex("/login"), new() { Timeout = 15_000 }).ConfigureAwait(false);
        await LoginAsync(email, newPassword).ConfigureAwait(false);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" })).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task ChangeEmail_ShouldUpdateEmail()
    {
        // Declared opt-in, not DOM-probed (see ProfileSupportsEmailChange): an app without the feature
        // simply passes — the same no-dynamic-skip convention as
        // AuthorizationTestsBase.RegisteredUser_AuthenticatedPage.
        if (!ProfileSupportsEmailChange)
        {
            return;
        }

        // Arrange
        await RegisterNewUserAsync().ConfigureAwait(false);
        var profilePage = new ProfilePage(Page);
        await profilePage.GotoAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);

        // The consumer declared email change supported — a missing field is now a real failure.
        var emailField = Page.GetByLabel("Email");
        await Expect(emailField).ToBeVisibleAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);

        var newEmail = $"newemail-{UniqueId()}@test.com";

        // Act
        await emailField.ClearAsync().ConfigureAwait(false);
        await emailField.FillAsync(newEmail).ConfigureAwait(false);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save Email" }).ClickAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);

        // Assert — reload and verify
        await profilePage.GotoAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
        var email = await emailField.InputValueAsync().ConfigureAwait(false);
        email.Should().Be(newEmail);
    }

    [Fact]
    public async Task ProfilePage_ShouldLoadWithUserData()
    {
        // Arrange
        var firstName = $"Prof{UniqueId()[..4]}";
        var lastName = $"User{UniqueId()[..4]}";
        await RegisterNewUserAsync(firstName, lastName).ConfigureAwait(false);

        // Act
        var profilePage = new ProfilePage(Page);
        await profilePage.GotoAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);

        // Assert — profile should be pre-filled with registration data
        var loadedFirstName = await profilePage.FirstNameField.InputValueAsync().ConfigureAwait(false);
        var loadedLastName = await profilePage.LastNameField.InputValueAsync().ConfigureAwait(false);
        loadedFirstName.Should().Be(firstName);
        loadedLastName.Should().Be(lastName);
    }

    [Fact]
    public async Task ProfilePage_ShouldHaveNoAccessibilityViolations()
    {
        // Arrange — register (auto-logs in) so the authenticated profile page is reachable
        await RegisterNewUserAsync().ConfigureAwait(false);
        var profilePage = new ProfilePage(Page);
        await profilePage.GotoAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);

        // Assert — axe-core finds zero WCAG 2.1 AA violations on the profile page. Scoped to the documented
        // WCAG 2.1 AA target (AxeOptions.Wcag21Aa); axe "best-practice" advisories are intentionally out of
        // scope so this gate fails only on real conformance violations — matching the gallery + consumer scans.
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa).ConfigureAwait(false);
    }
}
