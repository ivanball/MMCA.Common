using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Layout;
using MMCA.Common.UI.Services.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// bUnit tests for <see cref="NavMenu"/> — auth-aware rendering (login/register vs. logout), the
/// logout interaction, and role-gated nav-item filtering.
/// </summary>
public sealed class NavMenuTests : BunitTestBase
{
    private readonly Mock<IAuthUIService> _auth = new();

    public NavMenuTests()
    {
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton<IOptions<LayoutSettings>>(
            Options.Create(new LayoutSettings { BrandName = "TestBrand" }));
    }

    [Fact]
    public void WhenAnonymous_ShowsLoginAndRegister_NotLogout()
    {
        RenderMudProviders(); // CultureSwitcher's MudMenu (mobile top-row) needs the popover provider.
        var cut = RenderUnderTest<NavMenu>(_ => { });

        cut.Markup.Should().Contain("Login");
        cut.Markup.Should().Contain("Register");
        cut.Markup.Should().NotContain("Logout");
    }

    [Fact]
    public void WhenAuthenticated_ShowsLogoutAndUserName_NotLogin()
    {
        RenderMudProviders();
        var cut = RenderAs<NavMenu>(TestPrincipal.AuthenticatedUser(name: "Ada Lovelace"), _ => { });

        cut.Markup.Should().Contain("Logout");
        cut.Markup.Should().Contain("Ada Lovelace");
        cut.Markup.Should().NotContain(">Login<");
    }

    [Fact]
    public void ClickingLogout_CallsLogoutAndNavigatesToLogin()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        RenderMudProviders();
        var cut = RenderAs<NavMenu>(TestPrincipal.AuthenticatedUser(), _ => { });

        // An OnClick-only MudNavLink (no Href) renders as a <div class="mud-nav-link">, not an anchor.
        cut.FindAll(".mud-nav-link")
            .First(e => e.TextContent.Contains("Logout", StringComparison.OrdinalIgnoreCase))
            .Click();

        cut.WaitForAssertion(() => _auth.Verify(x => x.LogoutAsync(), Times.Once()));
        nav.Uri.Should().EndWith("/login");
    }

    [Fact]
    public void WhenAnonymous_HidesRoleGatedNavItems()
    {
        RegisterModule(
            new NavItem("Browse Catalog", "/catalog", "icon"),
            new NavItem("Manage Events", "/events", "icon", RequiredRole: "Organizer", Section: NavSection.Admin));

        RenderMudProviders();
        var cut = RenderUnderTest<NavMenu>(_ => { });

        cut.Markup.Should().Contain("Browse Catalog");
        cut.Markup.Should().NotContain("Manage Events");
    }

    [Fact]
    public void WhenOrganizer_ShowsRoleGatedNavItems()
    {
        RegisterModule(
            new NavItem("Browse Catalog", "/catalog", "icon"),
            new NavItem("Manage Events", "/events", "icon", RequiredRole: "Organizer", Section: NavSection.Admin));

        RenderMudProviders();
        var cut = RenderAs<NavMenu>(TestPrincipal.Organizer(), _ => { });

        cut.Markup.Should().Contain("Browse Catalog");
        cut.Markup.Should().Contain("Manage Events");
    }

    private void RegisterModule(params NavItem[] navItems)
        => Services.AddSingleton<IUIModule>(new StubUiModule(navItems));

    private sealed class StubUiModule(IReadOnlyList<NavItem> navItems) : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = navItems;

        public Assembly Assembly => typeof(StubUiModule).Assembly;
    }
}
