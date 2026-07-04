using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.Testing.E2E.Workflows.Identity;

/// <summary>
/// Authorization workflow fitness tests, authored once and re-run as a thin subclass in each consumer
/// repo (alongside the Login/Registration/Logout/Profile workflow bases): every path in
/// <see cref="ProtectedPaths"/> must bounce an anonymous full-page load to <c>/login</c>, every path in
/// <see cref="PublicPaths"/> must stay reachable anonymously, and (optionally) a freshly-registered
/// non-admin user must reach <see cref="AuthenticatedUserPath"/>. The subclass supplies its app's route
/// lists; the assertions and the SSR/client-side-navigation mechanics live here.
/// </summary>
public abstract class AuthorizationTestsBase : E2ETestBase
{
    protected AuthorizationTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>Paths that require authentication — an anonymous full-page load must redirect to /login.</summary>
    protected abstract IReadOnlyList<string> ProtectedPaths { get; }

    /// <summary>Paths reachable anonymously — a full-page load must stay on the requested path.</summary>
    protected abstract IReadOnlyList<string> PublicPaths { get; }

    /// <summary>
    /// A path a freshly-registered (non-admin) user must be able to reach via client-side navigation,
    /// or null when the app has no such representative page (the test is then skipped).
    /// </summary>
    protected virtual string? AuthenticatedUserPath => null;

    /// <summary>
    /// Admin-only paths — a freshly-registered regular user navigating client-side must be shown the
    /// shared Forbidden page (Routes.razor renders <c>&lt;Forbidden /&gt;</c> for an authenticated user
    /// lacking the required role), never the admin content. Empty when the app has no admin surface
    /// (the test then passes, same convention as <see cref="AuthenticatedUserPath"/>). This covers the
    /// escalation direction the anonymous-redirect test cannot: a logged-in non-admin probing admin routes.
    /// </summary>
    protected virtual IReadOnlyList<string> AdminPaths => [];

    [Fact]
    public async Task AnonymousUser_ProtectedPages_ShouldRedirectToLogin()
    {
        ProtectedPaths.Should().NotBeEmpty(
            "at least one protected path must be declared, so the authorization redirect is actually verified");

        foreach (var path in ProtectedPaths)
        {
            await Page.GotoAndWaitForBlazorAsync(path);
            await Page.WaitForLoadStateAsync(LoadState.Load);
            await Expect(Page).ToHaveURLAsync(new Regex("/login"), new() { Timeout = 15_000 });
        }
    }

    [Fact]
    public async Task AnonymousUser_PublicPages_ShouldBeAccessible()
    {
        PublicPaths.Should().NotBeEmpty(
            "at least one public path must be declared, so anonymous reachability is actually verified");

        foreach (var path in PublicPaths)
        {
            await Page.GotoAndWaitForBlazorAsync(path);
            await Page.WaitForLoadStateAsync(LoadState.Load);
            await Expect(Page).ToHaveURLAsync(new Regex(Regex.Escape(path)), new() { Timeout = 15_000 });
        }
    }

    [Fact]
    public async Task RegisteredUser_AuthenticatedPage_ShouldBeAccessible()
    {
        // A declared dynamic skip would need xunit.v3.assert, which this shipped fixture library
        // deliberately does not reference — an app with no representative page simply passes.
        if (AuthenticatedUserPath is not { } authenticatedPath)
        {
            return;
        }

        // Arrange — register as a regular (non-admin) user.
        await RegisterNewUserAsync();

        // Act — navigate client-side: SSR cannot read the JWT from browser storage, so a full page
        // load to an [Authorize] page would bounce to /login even when logged in.
        await Page.GotoProtectedAsync(authenticatedPath);

        // Assert — the page loads (even if it shows an empty-state alert).
        await Expect(Page).ToHaveURLAsync(new Regex(Regex.Escape(authenticatedPath)), new() { Timeout = 15_000 });
    }

    [Fact]
    public async Task RegisteredUser_AdminPages_ShouldBeForbidden()
    {
        // Same no-dynamic-skip convention as RegisteredUser_AuthenticatedPage: an app that declares no
        // admin surface simply passes.
        if (AdminPaths.Count == 0)
        {
            return;
        }

        // Arrange — register as a regular (non-admin) user.
        await RegisterNewUserAsync();

        foreach (var path in AdminPaths)
        {
            // Act — navigate client-side: SSR cannot read the JWT from browser storage, so a full page
            // load would bounce to /login and never exercise the ROLE check this test exists for.
            await Page.GotoProtectedAsync(path);

            // Assert — the shared Forbidden page renders (its h1 carries role="alert" and the localized
            // "Access Denied" title). Role denial is not a redirect: the URL stays on the requested
            // path, so the page CONTENT is the only reliable denial signal.
            await Expect(Page.Locator("h1[role='alert']"))
                .ToContainTextAsync("Access Denied", new() { Timeout = 15_000 });
        }
    }
}
