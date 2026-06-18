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

        // Optional full-speed trace capture (E2E_TRACE=<path>) — records network/DOM/console for offline
        // inspection of timing-sensitive failures that DevTools/slow-mo would mask.
        if (E2ETestConfiguration.TracePath is not null)
        {
            await _context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        }

        Page = await _context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (E2ETestConfiguration.TracePath is not null)
        {
            await _context.Tracing.StopAsync(new() { Path = E2ETestConfiguration.TracePath });
        }

        await Page.CloseAsync();
        await _context.DisposeAsync();
    }

    protected async Task LoginAsAdminAsync() =>
        await LoginAsync(E2ETestConfiguration.AdminCredentials.Email, E2ETestConfiguration.AdminCredentials.Password);

    protected async Task LoginAsUserAsync() =>
        await LoginAsync(E2ETestConfiguration.UserCredentials.Email, E2ETestConfiguration.UserCredentials.Password);

    protected async Task LoginAsync(string email, string password)
    {
        // If already authenticated, clear the existing session so the login page renders without a
        // pre-existing logout button that would cause the post-login wait to return before the new
        // credentials take effect. Cover BOTH token stores: localStorage (WASM/MAUI hosts) AND the
        // HttpOnly session cookie (the Blazor Server host is cookie-only, so a localStorage clear alone
        // is a no-op and the prior session would persist — leaving the next login authenticated as the
        // WRONG user). The DELETE hits the same /auth/session-cookie endpoint the app's logout uses.
        var logoutVisible = Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" });
        if (await logoutVisible.IsVisibleAsync())
        {
            await Page.EvaluateAsync(
                "async () => { localStorage.removeItem('auth_access_token'); localStorage.removeItem('auth_refresh_token');" +
                " try { await fetch('/auth/session-cookie', { method: 'DELETE', credentials: 'same-origin' }); } catch (e) { } }");
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
            logoutButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = E2ETestConfiguration.AuthTimeout }),
            errorAlert.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = E2ETestConfiguration.AuthTimeout }));

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
            logoutButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = E2ETestConfiguration.AuthTimeout }),
            regErrorAlert.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = E2ETestConfiguration.AuthTimeout }));

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
    /// Fills a form field and waits for the value to persist, guarding against the Blazor
    /// InteractiveAuto re-hydration race. Delegates to the single shared
    /// <see cref="PageExtensions.FillAndVerifyAsync"/> helper.
    /// </summary>
    protected static Task FillFieldAsync(ILocator field, string value) =>
        field.FillAndVerifyAsync(value);

    protected static string UniqueId() => Guid.NewGuid().ToString("N")[..8];
}
