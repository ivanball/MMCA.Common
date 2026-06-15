using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;

namespace MMCA.Common.Testing.E2E.PageObjects;

public sealed class RegisterPage
{
    private readonly IPage _page;

    public RegisterPage(IPage page) => _page = page;

    public ILocator FirstNameField => _page.GetByLabel("First Name");
    public ILocator LastNameField => _page.GetByLabel("Last Name");
    public ILocator EmailField => _page.GetByLabel("Email");
    public ILocator PasswordField => _page.GetByLabel("Password", new() { Exact = true });
    public ILocator ConfirmPasswordField => _page.GetByLabel("Confirm Password");
    public ILocator RegisterButton => _page.GetByRole(AriaRole.Button, new() { Name = "Create your account" });
    public ILocator ErrorAlert => _page.Locator(".mud-alert-text-error");

    // "Sign In" link is inside "Already have an account?" text
    public ILocator AlreadyHaveAccountLink => _page.GetByRole(AriaRole.Link, new() { Name = "Sign In" });

    // Address fields (inside expansion panel)
    public ILocator AddressPanel => _page.GetByText("Address (Optional)");
    public ILocator AddressLine1Field => _page.GetByLabel("Address Line 1");
    public ILocator CityField => _page.GetByLabel("City");
    public ILocator StateField => _page.GetByLabel("State");
    public ILocator ZipCodeField => _page.GetByLabel("Zip Code");
    public ILocator CountryField => _page.GetByLabel("Country");

    public async Task GotoAsync() =>
        await _page.GotoAndWaitForBlazorAsync("/register");

    public async Task RegisterAsync(string firstName, string lastName, string email, string password)
    {
        await FillFieldAsync(FirstNameField, firstName);
        await FillFieldAsync(LastNameField, lastName);
        await FillFieldAsync(EmailField, email);
        await FillFieldAsync(PasswordField, password);
        await FillFieldAsync(ConfirmPasswordField, password);
        await RegisterButton.ClickAsync();
    }

    /// <summary>
    /// Fills a field and waits for the value to stick (guards against the Blazor re-hydration race).
    /// Delegates to the single shared <see cref="Infrastructure.PageExtensions.FillAndVerifyAsync"/> helper.
    /// </summary>
    private static Task FillFieldAsync(ILocator field, string value) =>
        field.FillAndVerifyAsync(value);
}
