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
        }).ConfigureAwait(false);

        _context.SetDefaultTimeout(E2ETestConfiguration.DefaultTimeout);

        // Optional full-speed trace capture (E2E_TRACE=<path>) — records network/DOM/console for offline
        // inspection of timing-sensitive failures that DevTools/slow-mo would mask.
        if (E2ETestConfiguration.TracePath is not null)
        {
            await _context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true }).ConfigureAwait(false);
        }

        Page = await _context.NewPageAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (E2ETestConfiguration.TracePath is not null)
        {
            await StopTracingAsync(E2ETestConfiguration.TracePath).ConfigureAwait(false);
        }

        await Page.CloseAsync().ConfigureAwait(false);
        await _context.DisposeAsync().ConfigureAwait(false);
    }

    // Stops the Playwright trace. When E2E_TRACE names a DIRECTORY (per-test mode), a trace is written
    // named by the current test, but ONLY when that test failed: a full-suite run then yields just the
    // failing traces, each under its own name (no overwriting), which is what makes a scale-only failure
    // inspectable. A plain file path keeps the original single-file behavior (one test at a time).
    private async Task StopTracingAsync(string tracePath)
    {
        var isDirectory = Directory.Exists(tracePath)
            || tracePath.EndsWith('/')
            || tracePath.EndsWith('\\');
        if (!isDirectory)
        {
            await _context.Tracing.StopAsync(new() { Path = tracePath }).ConfigureAwait(false);
            return;
        }

        if (TestContext.Current.TestState?.Result == TestResult.Failed)
        {
            Directory.CreateDirectory(tracePath);
            var raw = TestContext.Current.Test?.TestDisplayName ?? "unknown";
            var safe = string.Concat(raw.Split(Path.GetInvalidFileNameChars()));
            await _context.Tracing.StopAsync(new() { Path = Path.Combine(tracePath, safe + ".zip") }).ConfigureAwait(false);
        }
        else
        {
            await _context.Tracing.StopAsync().ConfigureAwait(false);
        }
    }

    protected async Task LoginAsAdminAsync() =>
        await LoginAsync(E2ETestConfiguration.AdminCredentials.Email, E2ETestConfiguration.AdminCredentials.Password).ConfigureAwait(false);

    protected async Task LoginAsUserAsync() =>
        await LoginAsync(E2ETestConfiguration.UserCredentials.Email, E2ETestConfiguration.UserCredentials.Password).ConfigureAwait(false);

    protected async Task LoginAsync(string email, string password)
    {
        // If already authenticated, clear the existing session so the login page renders without a
        // pre-existing logout button that would cause the post-login wait to return before the new
        // credentials take effect. Cover BOTH token stores: localStorage (WASM/MAUI hosts) AND the
        // HttpOnly session cookie (the Blazor Server host is cookie-only, so a localStorage clear alone
        // is a no-op and the prior session would persist — leaving the next login authenticated as the
        // WRONG user). The DELETE hits the same /auth/session-cookie endpoint the app's logout uses.
        try
        {
            var logoutVisible = Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" });
            if (await logoutVisible.IsVisibleAsync().ConfigureAwait(false))
            {
                await Page.EvaluateAsync(
                    "async () => { localStorage.removeItem('auth_access_token'); localStorage.removeItem('auth_refresh_token');" +
                    " try { await fetch('/auth/session-cookie', { method: 'DELETE', credentials: 'same-origin' }); } catch (e) { } }").ConfigureAwait(false);
            }
        }
        catch (PlaywrightException)
        {
            // A caller-initiated navigation (e.g. a logout forceLoad still in flight) can destroy the
            // execution context mid-evaluate. That navigation is itself clearing the session, and the
            // GotoAndWaitForBlazorAsync below re-lands on a fresh /login deterministically, so the
            // cleanup's goal is met either way — don't fail the test over the race.
        }

        await Page.GotoAndWaitForBlazorAsync("/login").ConfigureAwait(false);

        // MudBlazor renders proper <label> elements — GetByLabel works.
        // Use FillFieldAsync to guard against Blazor re-hydration clearing values.
        await FillFieldAsync(Page.GetByLabel("Email"), email).ConfigureAwait(false);
        await FillFieldAsync(Page.GetByLabel("Password"), password).ConfigureAwait(false);

        // MudBlazor applies text-transform: uppercase — visible text is "LOGIN"
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in to your account" }).ClickAsync().ConfigureAwait(false);

        // On success, the Login page calls NavigateTo("/", forceLoad: true) — a full page reload away
        // from /login. On failure it stays at /login and shows an error alert.
        await WaitForAuthResultAsync("/login", "Login").ConfigureAwait(false);

        // Wait for the post-login "/" page to become interactive before returning (symmetric with
        // RegisterNewUserAsync). The forceLoad reload re-boots the runtime, and under WebAssembly the
        // client auth state (role claims parsed from the stored token) only populates once that boot
        // finishes; a caller that immediately client-side-navigates to an [Authorize] page (the
        // LoginAsAdmin -> protected create/list flows) would otherwise race a not-yet-authorized,
        // not-yet-rendered page.
        await WaitForInteractiveOrReloadAsync().ConfigureAwait(false);
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

        await Page.GotoAndWaitForBlazorAsync("/register").ConfigureAwait(false);

        await FillFieldAsync(Page.GetByLabel("First Name"), firstName).ConfigureAwait(false);
        await FillFieldAsync(Page.GetByLabel("Last Name"), lastName).ConfigureAwait(false);
        await FillFieldAsync(Page.GetByLabel("Email"), email).ConfigureAwait(false);
        await FillFieldAsync(Page.GetByLabel("Password", new() { Exact = true }), password).ConfigureAwait(false);
        await FillFieldAsync(Page.GetByLabel("Confirm Password"), password).ConfigureAwait(false);

        await Page.GetByRole(AriaRole.Button, new() { Name = "Create your account" }).ClickAsync().ConfigureAwait(false);

        // Registration does NavigateTo("/", forceLoad: true) on success: a full page reload away from
        // /register. On failure it stays on /register and shows an error alert. Do NOT call
        // WaitForBlazorAsync before the result settles: the forceLoad navigation destroys the JS context.
        await WaitForAuthResultAsync("/register", "Registration").ConfigureAwait(false);

        // The forceLoad reload lands on a freshly served "/" whose interactive runtime is still starting
        // (under WebAssembly that is a full CLR boot, slower than a Server circuit). Wait for interactivity
        // HERE so every caller gets an interactive page back, instead of each test having to remember it
        // (the post-register sign-out click / protected-page open would otherwise hit a non-interactive
        // DOM).
        await WaitForInteractiveOrReloadAsync().ConfigureAwait(false);

        return (email, password);
    }

    // Waits for the post-auth page to become interactive; if the wait fails, RELOADS once and waits
    // again rather than re-waiting on the same stuck page. Two failure modes feed this (both
    // trace-proven on the shared 2-core CI runner, ADC run 28589825631, 2026-07-02):
    // (1) a context-destroyed PlaywrightException from the in-flight forceLoad reload (the original
    //     guarded race), and (2) a TimeoutException when the freshly loaded page never initializes its
    //     runtime under contention (InteractiveAuto's second visit boots the downloaded WASM bundle,
    //     which can exceed the wait on a saturated host). The old catch handled only
    //     PlaywrightException, so Playwright's TimeoutException (which derives from
    //     System.TimeoutException, not PlaywrightException) skipped the retry entirely; and re-waiting
    //     without reloading just watched the same stalled boot. A reload issues a fresh request whose
    //     framework assets are now HTTP-cached, giving the retry a genuinely new attempt.
    private async Task WaitForInteractiveOrReloadAsync()
    {
        try
        {
            await Page.WaitForBlazorAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load }).ConfigureAwait(false);
            await Page.WaitForBlazorAsync().ConfigureAwait(false);
        }
    }

    // Waits for the post-submit auth result, racing THREE signals so success detection does NOT depend
    // on the interactive "Sign out" button having hydrated — which under Blazor Server-mode prerender on
    // a contended CI runner can lag well behind the successful forceLoad (the TD-06/07 register/login
    // contention failure mode). The forceLoad URL change away from the auth page (<paramref
    // name="authPagePath"/>, e.g. "/login" or "/register") is the interactivity-INDEPENDENT success
    // signal; only an error alert that is still showing ON the auth page after the grace window is a
    // real failure. Strictly safer than waiting on the logout button alone: it declares failure in a
    // subset of the prior conditions, so it cannot turn a passing flow into a failing one.
    private async Task WaitForAuthResultAsync(string authPagePath, string operation)
    {
        var logoutButton = Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" });
        var errorAlert = Page.Locator(".mud-alert-text-error");

        var leftAuthPage = Page.WaitForURLAsync(
            url => !url.Contains(authPagePath, StringComparison.OrdinalIgnoreCase),
            new() { Timeout = E2ETestConfiguration.AuthTimeout });

        await Task.WhenAny(
            leftAuthPage,
            logoutButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = E2ETestConfiguration.AuthTimeout }),
            errorAlert.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = E2ETestConfiguration.AuthTimeout })).ConfigureAwait(false);

        // Success is unambiguous once the forceLoad has navigated away from the auth page. Only treat an
        // error alert STILL on the auth page (after the grace window, with no navigation) as a failure.
        if (await errorAlert.IsVisibleAsync().ConfigureAwait(false)
            && Page.Url.Contains(authPagePath, StringComparison.OrdinalIgnoreCase)
            && !await AuthSucceededWithinGraceAsync(authPagePath, logoutButton).ConfigureAwait(false))
        {
            var errorText = await errorAlert.TextContentAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"{operation} failed: {errorText}");
        }
    }

    // Within the grace window, treats EITHER the forceLoad navigating away from the auth page OR the
    // interactive logout button appearing as success — so a transient error-alert flash during a slow
    // Server-mode success path is not mistaken for a real failure. See E2ETestConfiguration.AuthGraceTimeout.
    private async Task<bool> AuthSucceededWithinGraceAsync(string authPagePath, ILocator logoutButton)
    {
        try
        {
            await Page.WaitForURLAsync(
                url => !url.Contains(authPagePath, StringComparison.OrdinalIgnoreCase),
                new() { Timeout = E2ETestConfiguration.AuthGraceTimeout }).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // No navigation within the grace window. Playwright surfaces this as a navigation
            // PlaywrightException or, for WaitForURLAsync specifically, a System.TimeoutException, so catch
            // both. Fall back to the interactive logout-button signal: the failed-login path (e.g. the
            // deleted-account relogin) has neither navigation nor a logout button, so the caller then
            // raises the InvalidOperationException it expects instead of leaking this timeout.
            return await logoutButton.IsVisibleAsync().ConfigureAwait(false);
        }
    }

    protected async Task NavigateAndWaitAsync(string path) =>
        await Page.GotoAndWaitForBlazorAsync(path).ConfigureAwait(false);

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
    // Scans with the pager-combobox exception: a grid list page's sole combobox is its MudTablePager
    // "rows per page" select, which MudBlazor 9.6.0 renders without an accessible name (see
    // AxeOptions.Wcag21AaExceptMudPagerCombobox — accepted upstream limitation, not reachable from app
    // markup). Every other WCAG 2.1 AA rule still runs; non-grid scans (ScanAsync) stay fully strict.
    protected async Task ScanGridAsync()
    {
        await Expect(Page.Locator(".mud-table-body .mud-table-row").First)
            .ToBeVisibleAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);
        await Expect(Page.Locator("[role='progressbar']")).ToHaveCountAsync(0, new() { Timeout = 15_000 }).ConfigureAwait(false);
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21AaExceptMudPagerCombobox).ConfigureAwait(false);
    }

    // Scan the current (settled) page for WCAG 2.1 AA violations. Guards against any residual loading
    // bar so axe sees the stable DOM, not a transient loading state.
    protected async Task ScanAsync()
    {
        await Expect(Page.Locator("[role='progressbar']")).ToHaveCountAsync(0, new() { Timeout = 15_000 }).ConfigureAwait(false);
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa).ConfigureAwait(false);
    }
}
