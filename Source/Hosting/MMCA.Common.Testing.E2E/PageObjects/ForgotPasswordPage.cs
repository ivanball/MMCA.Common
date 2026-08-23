using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;

namespace MMCA.Common.Testing.E2E.PageObjects;

public sealed class ForgotPasswordPage
{
    private readonly IPage _page;

    public ForgotPasswordPage(IPage page) => _page = page;

    public ILocator EmailField => _page.GetByLabel("Email");
    public ILocator SubmitButton => _page.GetByRole(AriaRole.Button, new() { Name = "Send a password reset link" });

    // The page always lands on the same success alert, whether or not the address has an account.
    public ILocator ConfirmationAlert => _page.Locator(".mud-alert-text-success");

    // "Back to Sign In" is a MudButton with Href, so it renders as <a>, not <button>
    public ILocator BackToLoginLink => _page.GetByRole(AriaRole.Link, new() { Name = "Back to Sign In" });

    public async Task GotoAsync() =>
        await _page.GotoAndWaitForBlazorAsync("/forgot-password").ConfigureAwait(false);

    public async Task RequestResetAsync(string email)
    {
        await EmailField.FillAndVerifyAsync(email).ConfigureAwait(false);
        await SubmitButton.ClickAsync().ConfigureAwait(false);
    }
}
