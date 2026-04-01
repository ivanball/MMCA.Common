using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;

namespace MMCA.Common.Testing.E2E.PageObjects;

public sealed class ProfilePage
{
    private readonly IPage _page;

    public ProfilePage(IPage page) => _page = page;

    // Name section
    public ILocator FirstNameField => _page.GetByLabel("First Name");
    public ILocator LastNameField => _page.GetByLabel("Last Name");
    public ILocator SaveNameButton => _page.GetByRole(AriaRole.Button, new() { Name = "Save Name" });

    // Address section
    public ILocator AddressLine1Field => _page.GetByLabel("Address Line 1");
    public ILocator AddressLine2Field => _page.GetByLabel("Address Line 2");
    public ILocator CityField => _page.GetByLabel("City");
    public ILocator StateField => _page.GetByLabel("State");
    public ILocator ZipCodeField => _page.GetByLabel("Zip Code");
    public ILocator CountryField => _page.GetByLabel("Country");
    public ILocator SaveAddressButton => _page.GetByRole(AriaRole.Button, new() { Name = "Save Address" });

    // Password section
    public ILocator CurrentPasswordField => _page.GetByLabel("Current Password");
    public ILocator NewPasswordField => _page.GetByLabel("New Password", new() { Exact = true });
    public ILocator ConfirmNewPasswordField => _page.GetByLabel("Confirm New Password");
    public ILocator ChangePasswordButton => _page.GetByRole(AriaRole.Button, new() { Name = "Change Password" });

    public ILocator ErrorAlert => _page.GetByRole(AriaRole.Alert);

    public async Task GotoAsync() =>
        await _page.BlazorNavigateAsync("/profile");
}
