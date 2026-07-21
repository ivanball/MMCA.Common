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

    // "Create Account" is a MudButton with Href — renders as <a>, not <button>
    public ILocator CreateAccountLink => _page.GetByRole(AriaRole.Link, new() { Name = "Create Account" });

    public async Task GotoAsync() =>
        await _page.GotoAndWaitForBlazorAsync("/login").ConfigureAwait(false);

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
