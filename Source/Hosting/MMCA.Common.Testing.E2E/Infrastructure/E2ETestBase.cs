using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

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

    protected Task LoginAsAdminAsync() =>
        UseAuthenticatedSessionAsync(
            "admin",
            page => LoginViaUiAsync(
                page,
                E2ETestConfiguration.AdminCredentials.Email,
                E2ETestConfiguration.AdminCredentials.Password));

    protected Task LoginAsUserAsync() =>
        UseAuthenticatedSessionAsync(
            "user",
            page => LoginViaUiAsync(
                page,
                E2ETestConfiguration.UserCredentials.Email,
                E2ETestConfiguration.UserCredentials.Password));

    /// <summary>
    /// Logs in with an explicit credential pair via the real UI flow on the current page. Use this for the
    /// login-flow tests themselves (valid/invalid credentials). The seeded admin/user roles instead reuse a
    /// single cached session per role (see <see cref="LoginAsAdminAsync"/> / <see cref="LoginAsUserAsync"/>),
    /// so the suite does not pay one full auth round-trip PER test on a contended CI runner (TD-06/07).
    /// </summary>
    protected Task LoginAsync(string email, string password) =>
        LoginViaUiAsync(Page, email, password);

    // Reuses ONE authenticated session per role across the collection: the first call for a role performs a
    // single real UI login (in the fixture) and captures its storageState (auth cookie + any localStorage
    // tokens); later calls just open a fresh context seeded with that state, so no further server-side auth
    // round-trip happens. Then lands on "/" where a real login would, so callers behave identically.
    private async Task UseAuthenticatedSessionAsync(string roleKey, Func<IPage, Task> performLogin)
    {
        var storageState = await _fixture.GetAuthenticatedStorageStateAsync(roleKey, performLogin);
        await ReplaceContextWithStateAsync(storageState);
        await Page.GotoAndWaitForBlazorAsync("/");
    }

    // Swaps the current (anonymous) context/page for one seeded from a captured authenticated storageState,
    // keeping the same context options + optional tracing as InitializeAsync.
    private async Task ReplaceContextWithStateAsync(string storageState)
    {
        if (E2ETestConfiguration.TracePath is not null)
        {
            await _context.Tracing.StopAsync();
        }

        await Page.CloseAsync();
        await _context.DisposeAsync();

        _context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = BaseUrl,
            StorageState = storageState,
        });
        _context.SetDefaultTimeout(E2ETestConfiguration.DefaultTimeout);

        if (E2ETestConfiguration.TracePath is not null)
        {
            await _context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        }

        Page = await _context.NewPageAsync();
    }

    // The real UI login flow, factored to a static so the fixture can run it ONCE per role to capture the
    // reusable session and the explicit-credential LoginAsync can share it. Operates on the given page.
    private static async Task LoginViaUiAsync(IPage page, string email, string password)
    {
        // If already authenticated, clear the existing session so the login page renders without a
        // pre-existing logout button that would cause the post-login wait to return before the new
        // credentials take effect. Cover BOTH token stores: localStorage (WASM/MAUI hosts) AND the
        // HttpOnly session cookie (the Blazor Server host is cookie-only, so a localStorage clear alone is
        // a no-op and the prior session would persist). The DELETE hits the same /auth/session-cookie
        // endpoint the app's logout uses.
        var logoutVisible = page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" });
        if (await logoutVisible.IsVisibleAsync())
        {
            await page.EvaluateAsync(
                "async () => { localStorage.removeItem('auth_access_token'); localStorage.removeItem('auth_refresh_token');" +
                " try { await fetch('/auth/session-cookie', { method: 'DELETE', credentials: 'same-origin' }); } catch (e) { } }");
        }

        await page.GotoAndWaitForBlazorAsync("/login");

        // MudBlazor renders proper <label> elements — GetByLabel works. FillAndVerifyAsync guards against
        // Blazor re-hydration clearing values.
        await page.GetByLabel("Email").FillAndVerifyAsync(email);
        await page.GetByLabel("Password").FillAndVerifyAsync(password);

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in to your account" }).ClickAsync();

        // On success, the Login page calls NavigateTo("/", forceLoad: true) — a full reload away from
        // /login. On failure it stays at /login and shows an error alert.
        await WaitForAuthResultAsync(page, "/login", "Login");
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

        // Registration does NavigateTo("/", forceLoad: true) on success — a full page reload away from
        // /register. On failure it stays on /register and shows an error alert. Do NOT call
        // WaitForBlazorAsync here — the forceLoad navigation destroys the JS execution context.
        await WaitForAuthResultAsync(Page, "/register", "Registration");

        return (email, password);
    }

    // Waits for the post-submit auth result, racing THREE signals so success detection does NOT depend
    // on the interactive "Sign out" button having hydrated — which under Blazor Server-mode prerender on
    // a contended CI runner can lag well behind the successful forceLoad (the TD-06/07 register/login
    // contention failure mode). The forceLoad URL change away from the auth page (<paramref
    // name="authPagePath"/>, e.g. "/login" or "/register") is the interactivity-INDEPENDENT success
    // signal; only an error alert that is still showing ON the auth page after the grace window is a
    // real failure. Strictly safer than waiting on the logout button alone: it declares failure in a
    // subset of the prior conditions, so it cannot turn a passing flow into a failing one.
    private static async Task WaitForAuthResultAsync(IPage page, string authPagePath, string operation)
    {
        var logoutButton = page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" });
        var errorAlert = page.Locator(".mud-alert-text-error");

        var leftAuthPage = page.WaitForURLAsync(
            url => !url.Contains(authPagePath, StringComparison.OrdinalIgnoreCase),
            new() { Timeout = E2ETestConfiguration.AuthTimeout });

        await Task.WhenAny(
            leftAuthPage,
            logoutButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = E2ETestConfiguration.AuthTimeout }),
            errorAlert.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = E2ETestConfiguration.AuthTimeout }));

        // Success is unambiguous once the forceLoad has navigated away from the auth page. Only treat an
        // error alert STILL on the auth page (after the grace window, with no navigation) as a failure.
        if (await errorAlert.IsVisibleAsync()
            && page.Url.Contains(authPagePath, StringComparison.OrdinalIgnoreCase)
            && !await AuthSucceededWithinGraceAsync(page, authPagePath, logoutButton))
        {
            var errorText = await errorAlert.TextContentAsync();
            throw new InvalidOperationException($"{operation} failed: {errorText}");
        }
    }

    // Within the grace window, treats EITHER the forceLoad navigating away from the auth page OR the
    // interactive logout button appearing as success — so a transient error-alert flash during a slow
    // Server-mode success path is not mistaken for a real failure. See E2ETestConfiguration.AuthGraceTimeout.
    private static async Task<bool> AuthSucceededWithinGraceAsync(IPage page, string authPagePath, ILocator logoutButton)
    {
        try
        {
            await page.WaitForURLAsync(
                url => !url.Contains(authPagePath, StringComparison.OrdinalIgnoreCase),
                new() { Timeout = E2ETestConfiguration.AuthGraceTimeout });
            return true;
        }
        catch (PlaywrightException)
        {
            // No navigation within grace — fall back to the interactive logout-button signal.
            return await logoutButton.IsVisibleAsync();
        }
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

    // Scan a MudDataGrid list page. The grid renders its container before its async ServerData load
    // fires, so we must wait for a post-load signal (a data row) before scanning. Waiting for the
    // loading bar to "hide" is racy: with no bar present yet that wait resolves instantly, then the
    // transient unnamed role="progressbar" loading bar appears and trips aria-progressbar-name. Every
    // scanned list page is seeded with at least one row, so a visible row reliably means the load settled.
    protected async Task ScanGridAsync()
    {
        await Expect(Page.Locator(".mud-table-body .mud-table-row").First)
            .ToBeVisibleAsync(new() { Timeout = 15_000 });
        await ScanAsync();
    }

    // Scan the current (settled) page for WCAG 2.1 AA violations. Guards against any residual loading
    // bar so axe sees the stable DOM, not a transient loading state.
    protected async Task ScanAsync()
    {
        await Expect(Page.Locator("[role='progressbar']")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }
}
