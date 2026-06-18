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
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out of your account" }).ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);

        // Act — try to access a protected page. The app's RedirectToLogin component
        // does NavigateTo("/login", forceLoad: true), which causes a second navigation.
        // Navigate and wait for the final destination.
        await Page.GotoAsync("/profile");
        await Page.WaitForLoadStateAsync(LoadState.Load);
        await Page.WaitForBlazorAsync();

        // Assert — should redirect to login or show unauthorized. The RedirectToLogin component
        // runs after the protected page hydrates and reads the cleared token, so on a cold circuit
        // the client-side redirect can take longer than the 5s default — allow up to 15s.
        await Expect(Page).ToHaveURLAsync(new Regex("/login"), new() { Timeout = 15_000 });
    }
}
