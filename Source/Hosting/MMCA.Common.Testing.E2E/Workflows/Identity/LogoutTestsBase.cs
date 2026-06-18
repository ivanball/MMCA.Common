using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.Testing.E2E.Workflows.Identity;

public abstract class LogoutTestsBase : E2ETestBase
{
    protected LogoutTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Logout_ShouldRedirectToLoginPage()
    {
        // Arrange — register and login
        await RegisterNewUserAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" })).ToBeVisibleAsync();

        // Act
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" }).ClickAsync();

        // Assert — should be on login page
        await Page.WaitForLoadStateAsync(LoadState.Load);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign in to your account" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Logout_ShouldPreventAccessToProtectedPages()
    {
        // Arrange — login then logout
        await RegisterNewUserAsync();

        // Ensure the circuit is interactive before logging out — RegisterNewUserAsync returns right after
        // a forceLoad reload (it deliberately skips WaitForBlazor), so the sign-out button can be visible
        // before JS interop is ready.
        await Page.WaitForBlazorAsync();

        // Click sign-out and wait for its cookie-clear request to COMPLETE before probing a protected
        // page. Logout fires a best-effort DELETE /auth/session-cookie via JS interop; at full speed the
        // test otherwise reaches /profile before that fetch finishes, so the HttpOnly session cookie is
        // still present and SSR re-authenticates. Waiting for the DELETE response makes logout
        // deterministic — this is the timing that E2E_SLOWMO's between-action pauses masked.
        await Page.RunAndWaitForResponseAsync(
            async () => await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" }).ClickAsync(),
            response => response.Url.Contains("/auth/session-cookie", StringComparison.OrdinalIgnoreCase)
                && string.Equals(response.Request.Method, "DELETE", StringComparison.OrdinalIgnoreCase),
            new() { Timeout = 15_000 });

        // The session cookie is now cleared; confirm we've landed on /login.
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign in to your account" }))
            .ToBeVisibleAsync(new() { Timeout = 15_000 });

        // Act + Assert — a protected page must redirect to /login now we're logged out. At full machine
        // speed the logout's cookie-clear can lag this navigation by a few ms, so the FIRST request may
        // still carry the (already server-side-revoked) cookie and render /profile. The session is
        // revoked, so re-request until the server redirects: each GotoAsync is a fresh request reflecting
        // the current cookie store, and WaitForBlazorAsync's render-settle lets the cookie-clear
        // propagate — it converges within a couple of attempts. (Any slowdown — slow-mo, or even trace
        // capture — hides this race, so the bounded retry makes it deterministic at full speed.)
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Page.GotoAsync("/profile");
            await Page.WaitForBlazorAsync();
            if (Page.Url.Contains("/login", StringComparison.Ordinal))
            {
                return;
            }
        }

        // Never redirected after several fresh requests — fail with a clear URL assertion.
        await Expect(Page).ToHaveURLAsync(new Regex("/login"), new() { Timeout = 5_000 });
    }
}
