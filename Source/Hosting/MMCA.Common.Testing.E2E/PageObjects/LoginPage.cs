using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;

namespace MMCA.Common.Testing.E2E.PageObjects;

public sealed class LoginPage
{
    private readonly IPage _page;

    public LoginPage(IPage page) => _page = page;

    public ILocator EmailField => _page.GetByLabel("Email");
    public ILocator PasswordField => _page.GetByLabel("Password");
    public ILocator LoginButton => _page.GetByRole(AriaRole.Button, new() { Name = "Sign in to your account" });
    public ILocator ErrorAlert => _page.Locator(".mud-alert-text-error");

    /// <summary>
    /// The notice the page shows when registration sent the visitor here because the typed address
    /// already has an account (the address arrives prefilled).
    /// </summary>
    public ILocator ExistingAccountNotice => _page.GetByTestId("login-existing-account");

    /// <summary>The Reset password button inside <see cref="ExistingAccountNotice"/>.</summary>
    public ILocator ResetPasswordButton => _page.GetByTestId("login-reset-password");

    // "Create Account" is a MudButton with Href — renders as <a>, not <button>
    public ILocator CreateAccountLink => _page.GetByRole(AriaRole.Link, new() { Name = "Create Account" });

    public Task GotoAsync() =>
        _page.GotoAndWaitForBlazorAsync("/login");

    public async Task LoginAsync(string email, string password)
    {
        await FillFieldAsync(EmailField, email).ConfigureAwait(false);
        await FillFieldAsync(PasswordField, password).ConfigureAwait(false);
        await LoginButton.ClickAsync().ConfigureAwait(false);
    }

    // Single shared fill helper guards against the Blazor re-hydration race (auto-waiting, no fixed delays).
    private static Task FillFieldAsync(ILocator field, string value) =>
        field.FillAndVerifyAsync(value);
}
