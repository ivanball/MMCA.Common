using Microsoft.Playwright;
using Xunit;

namespace MMCA.Common.Testing.E2E.Infrastructure;

[Collection(E2ETestCollection.Name)]
public abstract class E2ETestBase : IAsyncLifetime
{
    private readonly PlaywrightFixture _fixture;
    private IBrowserContext _context = null!;

    protected IPage Page { get; private set; } = null!;
    protected static string BaseUrl => E2ETestConfiguration.BaseUrl;

    protected E2ETestBase(PlaywrightFixture fixture) =>
        _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        _context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = BaseUrl,
        });

        _context.SetDefaultTimeout(E2ETestConfiguration.DefaultTimeout);
        Page = await _context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await Page.CloseAsync();
        await _context.DisposeAsync();
    }

    protected async Task LoginAsAdminAsync() =>
        await LoginAsync(E2ETestConfiguration.AdminCredentials.Email, E2ETestConfiguration.AdminCredentials.Password);

    protected async Task LoginAsUserAsync() =>
        await LoginAsync(E2ETestConfiguration.UserCredentials.Email, E2ETestConfiguration.UserCredentials.Password);

    protected async Task LoginAsync(string email, string password)
    {
        // If already authenticated, clear the existing session so the login page
        // renders without a pre-existing logout button that would cause the
        // post-login wait to return before the new credentials take effect.
        var logoutVisible = Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" });
        if (await logoutVisible.IsVisibleAsync())
        {
            await Page.EvaluateAsync("() => { localStorage.removeItem('auth_access_token'); localStorage.removeItem('auth_refresh_token'); }");
        }

        await Page.GotoAndWaitForBlazorAsync("/login");

        // MudBlazor renders proper <label> elements — GetByLabel works.
        // Use FillFieldAsync to guard against Blazor re-hydration clearing values.
        await FillFieldAsync(Page.GetByLabel("Email"), email);
        await FillFieldAsync(Page.GetByLabel("Password"), password);

        // MudBlazor applies text-transform: uppercase — visible text is "LOGIN"
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in to your account" }).ClickAsync();

        // On success, the Login page calls NavigateTo("/", forceLoad: true) which triggers
        // a full page reload to the home page. On failure, it stays at /login and shows
        // an error alert. Wait for the end result directly — this survives both full page
        // reloads and Blazor enhanced navigation without relying on Playwright navigation
        // event detection (which can miss history.pushState changes).
        var logoutButton = Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" });
        var errorAlert = Page.Locator(".mud-alert-text-error");

        var visibleFirst = await Task.WhenAny(
            logoutButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 }),
            errorAlert.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 }));

        await visibleFirst;

        if (await errorAlert.IsVisibleAsync())
        {
            var errorText = await errorAlert.TextContentAsync();
            throw new InvalidOperationException($"Login failed: {errorText}");
        }

    }

    protected async Task<(string Email, string Password)> RegisterNewUserAsync(
        string? firstName = null,
        string? lastName = null)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var email = $"e2e-{uniqueId}@test.com";
        const string password = "TestPass123!";
        firstName ??= $"E2E{uniqueId[..4]}";
        lastName ??= $"User{uniqueId[4..]}";

        await Page.GotoAndWaitForBlazorAsync("/register");

        await FillFieldAsync(Page.GetByLabel("First Name"), firstName);
        await FillFieldAsync(Page.GetByLabel("Last Name"), lastName);
        await FillFieldAsync(Page.GetByLabel("Email"), email);
        await FillFieldAsync(Page.GetByLabel("Password", new() { Exact = true }), password);
        await FillFieldAsync(Page.GetByLabel("Confirm Password"), password);

        await Page.GetByRole(AriaRole.Button, new() { Name = "Create your account" }).ClickAsync();

        // Registration does NavigateTo("/", forceLoad: true) on success which triggers a
        // full page reload. On failure, it stays on /register and shows an error alert.
        // Do NOT call WaitForBlazorAsync here — the forceLoad navigation destroys the JS
        // execution context. Instead, wait directly for the end result.
        var logoutButton = Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" });
        var regErrorAlert = Page.Locator(".mud-alert-text-error");

        var visibleFirst = await Task.WhenAny(
            logoutButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 }),
            regErrorAlert.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 }));

        await visibleFirst;

        if (await regErrorAlert.IsVisibleAsync())
        {
            var errorText = await regErrorAlert.TextContentAsync();
            throw new InvalidOperationException($"Registration failed: {errorText}");
        }

        return (email, password);
    }

    protected async Task NavigateAndWaitAsync(string path) =>
        await Page.GotoAndWaitForBlazorAsync(path);

    /// <summary>
    /// Fills a form field and verifies the value persisted. Blazor InteractiveAuto can
    /// re-hydrate the page after the runtime loads, replacing pre-rendered inputs and
    /// wiping any values filled before hydration completed. This method retries the fill
    /// if the value doesn't stick.
    /// </summary>
    protected static async Task FillFieldAsync(ILocator field, string value, int maxRetries = 5)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            await field.FillAsync(value);
            await Task.Delay(300);
            if (await field.InputValueAsync() == value)
                return;
            await field.ClearAsync();
            await field.PressSequentiallyAsync(value, new() { Delay = 20 });
            await Task.Delay(300);
            if (await field.InputValueAsync() == value)
                return;
            await Task.Delay(500);
        }
    }

    protected static string UniqueId() => Guid.NewGuid().ToString("N")[..8];
}
