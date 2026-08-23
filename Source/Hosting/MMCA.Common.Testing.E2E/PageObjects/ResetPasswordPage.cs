using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;

namespace MMCA.Common.Testing.E2E.PageObjects;

public sealed class ResetPasswordPage
{
    private readonly IPage _page;

    public ResetPasswordPage(IPage page) => _page = page;

    public ILocator EmailField => _page.GetByLabel("Email");
    public ILocator TokenField => _page.GetByLabel("Reset Token");

    // Exact: "New Password" is a substring of "Confirm New Password", so the default
    // substring match would resolve to both fields.
    public ILocator NewPasswordField => _page.GetByLabel("New Password", new() { Exact = true });
    public ILocator ConfirmPasswordField => _page.GetByLabel("Confirm New Password");

    public ILocator SubmitButton => _page.GetByRole(AriaRole.Button, new() { Name = "Reset your password" });
    public ILocator ErrorAlert => _page.Locator(".mud-alert-text-error");
    public ILocator SuccessAlert => _page.Locator(".mud-alert-text-success");

    // "Go to Sign In" is a MudButton with Href, so it renders as <a>, not <button>
    public ILocator GoToLoginLink => _page.GetByRole(AriaRole.Link, new() { Name = "Go to Sign In" });

    public async Task GotoAsync() =>
        await _page.GotoAndWaitForBlazorAsync("/reset-password").ConfigureAwait(false);

    /// <summary>
    /// Opens the page the way the emailed link does, with the address and token supplied as query
    /// parameters so both fields arrive prefilled.
    /// </summary>
    public async Task GotoWithLinkAsync(string email, string token) =>
        await _page.GotoAndWaitForBlazorAsync(
            $"/reset-password?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}").ConfigureAwait(false);

    public async Task ResetAsync(string email, string token, string newPassword)
    {
        await EmailField.FillAndVerifyAsync(email).ConfigureAwait(false);
        await TokenField.FillAndVerifyAsync(token).ConfigureAwait(false);
        await NewPasswordField.FillAndVerifyAsync(newPassword).ConfigureAwait(false);
        await ConfirmPasswordField.FillAndVerifyAsync(newPassword).ConfigureAwait(false);
        await SubmitButton.ClickAsync().ConfigureAwait(false);
    }
}
