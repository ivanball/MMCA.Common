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

    /// <summary>
    /// The warning alert the page shows when the typed address already has an account (with the
    /// sign-in and reset-password links). It is NOT <see cref="ErrorAlert"/>: since v1.196.0 the
    /// duplicate-address conflict renders as a warning, not the generic red error alert.
    /// </summary>
    public ILocator EmailAlreadyRegisteredAlert => _page.GetByTestId("email-already-registered");

    // "Sign In" link is inside "Already have an account?" text
    public ILocator AlreadyHaveAccountLink => _page.GetByRole(AriaRole.Link, new() { Name = "Sign In" });

    /// <summary>
    /// The required Terms acceptance checkbox. The page renders it only when the host configures a
    /// Terms URL (<c>Legal:TermsUrl</c>), and keeps "Create your account" disabled until it is ticked.
    /// The test id sits on the wrapper around the MudCheckBox, so this targets the input inside it.
    /// </summary>
    public ILocator TermsCheckbox => _page.Locator("[data-testid='register-accept-terms'] input[type='checkbox']");

    // Address fields (inside expansion panel)
    public ILocator AddressPanel => _page.GetByText("Address (Optional)");
    public ILocator AddressLine1Field => _page.GetByLabel("Address Line 1");
    public ILocator CityField => _page.GetByLabel("City");
    public ILocator StateField => _page.GetByLabel("State");
    public ILocator ZipCodeField => _page.GetByLabel("Zip Code");
    public ILocator CountryField => _page.GetByLabel("Country");

    public async Task GotoAsync() =>
        await _page.GotoAndWaitForBlazorAsync("/register").ConfigureAwait(false);

    public async Task RegisterAsync(string firstName, string lastName, string email, string password)
    {
        await FillFieldAsync(FirstNameField, firstName).ConfigureAwait(false);
        await FillFieldAsync(LastNameField, lastName).ConfigureAwait(false);
        await FillFieldAsync(EmailField, email).ConfigureAwait(false);
        await FillFieldAsync(PasswordField, password).ConfigureAwait(false);
        await FillFieldAsync(ConfirmPasswordField, password).ConfigureAwait(false);
        await AcceptTermsIfShownAsync().ConfigureAwait(false);
        await RegisterButton.ClickAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Ticks <see cref="TermsCheckbox"/> when the page renders it, and does nothing otherwise. The
    /// check is a single count of the rendered DOM, never a wait for the box to be absent, so a host
    /// without Terms pays nothing. Call it after the page is interactive (after <see cref="GotoAsync"/>):
    /// the box is rendered with the form, so a zero count then means the host has no Terms URL.
    /// </summary>
    public async Task AcceptTermsIfShownAsync()
    {
        if (await TermsCheckbox.CountAsync().ConfigureAwait(false) > 0)
        {
            // Force, as with RoleAdminPage.PermissionCheckbox: MudBlazor overlays its own icon on the
            // native input. SetChecked still verifies the box ended up checked.
            await TermsCheckbox.SetCheckedAsync(true, new() { Force = true }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fills a field and waits for the value to stick (guards against the Blazor re-hydration race).
    /// Delegates to the single shared <see cref="Infrastructure.PageExtensions.FillAndVerifyAsync"/> helper.
    /// </summary>
    private static Task FillFieldAsync(ILocator field, string value) =>
        field.FillAndVerifyAsync(value);
}
