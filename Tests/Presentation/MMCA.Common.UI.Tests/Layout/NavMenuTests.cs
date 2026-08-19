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

        // Exactly once: the name belongs to the hamburger menu's auth section only. The mobile
        // top-row used to render a second copy (with a duplicate title attribute), which on a phone
        // showed the same name twice and squeezed the narrowest row in the layout.
        cut.FindAll(".nav-user-identity").Should().ContainSingle();
        cut.FindAll(".toprow-actions").Should().ContainSingle();
        cut.Find(".toprow-actions").TextContent.Should().NotContain("Ada Lovelace");
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

    [Fact]
    public void WithoutBrandLogoUrl_RendersTheTextOnlyBrand()
    {
        RenderMudProviders();
        var cut = RenderUnderTest<NavMenu>(_ => { });

        cut.FindAll(".navbar-brand-logo").Should().BeEmpty(
            "LayoutSettings.BrandLogoUrl defaults to empty, so the brand stays text-only");
        cut.Find(".navbar-brand-text").TextContent.Should().Be("TestBrand");
    }

    [Fact]
    public void WithBrandLogoUrl_RendersADecorativeLogoBesideTheBrandText()
    {
        // Last registration wins, so this replaces the text-only settings from the constructor.
        Services.AddSingleton<IOptions<LayoutSettings>>(Options.Create(
            new LayoutSettings { BrandName = "TestBrand", BrandLogoUrl = "/img/brand.svg" }));

        RenderMudProviders();
        var cut = RenderUnderTest<NavMenu>(_ => { });

        var logo = cut.Find(".navbar-brand .navbar-brand-logo");
        logo.GetAttribute("src").Should().Be("/img/brand.svg");
        logo.GetAttribute("alt").Should().BeEmpty(
            "the logo is decorative: the brand link already carries its own accessible name");
        cut.Find(".navbar-brand-text").TextContent.Should().Be("TestBrand");
    }

    private void RegisterModule(params NavItem[] navItems)
        => Services.AddSingleton<IUIModule>(new StubUiModule(navItems));

    private sealed class StubUiModule(IReadOnlyList<NavItem> navItems) : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = navItems;

        public Assembly Assembly => typeof(StubUiModule).Assembly;
    }
}
