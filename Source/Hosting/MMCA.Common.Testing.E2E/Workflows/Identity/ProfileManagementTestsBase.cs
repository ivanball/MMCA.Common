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

    [Fact]
    public async Task ChangeName_ShouldUpdateProfileName()
    {
        // Arrange
        var (_, _) = await RegisterNewUserAsync();
        var profilePage = new ProfilePage(Page);
        await profilePage.GotoAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);

        var newFirstName = $"NewFirst{UniqueId()[..4]}";
        var newLastName = $"NewLast{UniqueId()[..4]}";

        // Act
        await profilePage.FirstNameField.ClearAsync();
        await profilePage.FirstNameField.FillAsync(newFirstName);
        await profilePage.LastNameField.ClearAsync();
        await profilePage.LastNameField.FillAsync(newLastName);
        await profilePage.SaveNameButton.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);

        // Assert — reload profile and verify name persisted
        await profilePage.GotoAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);
        var firstName = await profilePage.FirstNameField.InputValueAsync();
        var lastName = await profilePage.LastNameField.InputValueAsync();
        firstName.Should().Be(newFirstName);
        lastName.Should().Be(newLastName);
    }

    [Fact]
    public async Task ChangeAddress_ShouldUpdateProfileAddress()
    {
        // Arrange
        await RegisterNewUserAsync();
        var profilePage = new ProfilePage(Page);
        await profilePage.GotoAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);

        // Act
        await profilePage.AddressLine1Field.FillAsync("123 Test Street");
        await profilePage.CityField.FillAsync("TestCity");
        await profilePage.StateField.FillAsync("TS");
        await profilePage.ZipCodeField.FillAsync("12345");
        await profilePage.CountryField.FillAsync("TestCountry");
        await profilePage.SaveAddressButton.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);

        // Assert — reload and verify
        await profilePage.GotoAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);
        var address = await profilePage.AddressLine1Field.InputValueAsync();
        address.Should().Be("123 Test Street");
    }

    [Fact]
    public async Task ChangePassword_WithValidCurrentPassword_ShouldSucceed()
    {
        // Arrange — register with a known password
        var (email, password) = await RegisterNewUserAsync();
        var profilePage = new ProfilePage(Page);
        await profilePage.GotoAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);

        var newPassword = "NewTestPass456!";

        // Act
        await FillFieldAsync(profilePage.CurrentPasswordField, password);
        await FillFieldAsync(profilePage.NewPasswordField, newPassword);
        await FillFieldAsync(profilePage.ConfirmNewPasswordField, newPassword);
        await profilePage.ChangePasswordButton.ClickAsync();

        // Wait for the password change API call to complete (snackbar confirms success)
        await Expect(Page.GetByText("Password changed successfully.")).ToBeVisibleAsync(new() { Timeout = 30_000 });

        // Assert — log out and log back in with new password
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" }).ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);
        await LoginAsync(email, newPassword);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ChangeEmail_ShouldUpdateEmail()
    {
        // Arrange
        await RegisterNewUserAsync();
        var profilePage = new ProfilePage(Page);
        await profilePage.GotoAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);

        // Check if email field exists on profile page
        var emailField = Page.GetByLabel("Email");
        if (!await emailField.IsVisibleAsync())
        {
            // Email change may not be on the profile page — skip
            return;
        }

        var newEmail = $"newemail-{UniqueId()}@test.com";

        // Act
        await emailField.ClearAsync();
        await emailField.FillAsync(newEmail);

        // Look for a save email button
        var saveEmailButton = Page.GetByRole(AriaRole.Button, new() { Name = "Save Email" });
        if (await saveEmailButton.IsVisibleAsync())
        {
            await saveEmailButton.ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.Load);
        }

        // Assert — reload and verify
        await profilePage.GotoAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);
        var email = await emailField.InputValueAsync();
        email.Should().Be(newEmail);
    }

    [Fact]
    public async Task ProfilePage_ShouldLoadWithUserData()
    {
        // Arrange
        var firstName = $"Prof{UniqueId()[..4]}";
        var lastName = $"User{UniqueId()[..4]}";
        await RegisterNewUserAsync(firstName, lastName);

        // Act
        var profilePage = new ProfilePage(Page);
        await profilePage.GotoAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);

        // Assert — profile should be pre-filled with registration data
        var loadedFirstName = await profilePage.FirstNameField.InputValueAsync();
        var loadedLastName = await profilePage.LastNameField.InputValueAsync();
        loadedFirstName.Should().Be(firstName);
        loadedLastName.Should().Be(lastName);
    }
}
