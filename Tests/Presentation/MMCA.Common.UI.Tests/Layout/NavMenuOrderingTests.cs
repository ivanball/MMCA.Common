using System.Reflection;
using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Layout;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// bUnit tests for how <see cref="NavMenu"/> arranges a section: the default first-appearance order
/// (which every app that declares no <see cref="IUIModule.NavGroups"/> and no
/// <see cref="NavItem.Order"/> must keep), group metadata (icon, initially expanded, order) declared
/// by key from any module, and item order within a group or the section's ungrouped items.
/// </summary>
public sealed class NavMenuOrderingTests : BunitTestBase
{
    private const string Folder = Icons.Material.Filled.Folder;
    private const string EventIcon = Icons.Material.Filled.Event;
    private const string StarIcon = Icons.Material.Filled.Star;

    public NavMenuOrderingTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton<IOptions<LayoutSettings>>(
            Options.Create(new LayoutSettings { BrandName = "TestBrand" }));
    }

    [Fact]
    public void WithoutGroupMetadataOrItemOrder_KeepsFirstAppearanceOrder_CollapsedWithTheFolderIcon()
    {
        RegisterModule(
            [],
            Item("Alpha"),
            Item("Beta", group: "Tools"),
            Item("Gamma"),
            Item("Delta", group: "Tools"),
            Item("Admin One", NavSection.Admin, "Setup"),
            Item("Admin Two", NavSection.Admin),
            Item("Admin Three", NavSection.Admin, "Reports"));
        RegisterModule(
            [],
            Item("Epsilon", group: "Extras"),
            Item("Admin Four", NavSection.Admin, "Setup"));

        var cut = RenderSignedIn();

        Outline(cut).Should().Equal(
            "Home",
            "Alpha",
            "Gamma",
            "[Tools]",
            "Tools/Beta",
            "Tools/Delta",
            "[Extras]",
            "Extras/Epsilon",
            "#Administration",
            "[Setup]",
            "Setup/Admin One",
            "Setup/Admin Four",
            "Admin Two",
            "[Reports]",
            "Reports/Admin Three");

        foreach (var group in cut.FindAll(".mud-nav-group"))
        {
            IsExpanded(group).Should().BeFalse("an undeclared group starts collapsed, as it always has");
            GroupIcon(group).Should().Be(IconPath(Folder), "an undeclared group keeps the folder icon");
        }
    }

    [Fact]
    public void ADeclaredGroup_RendersItsIconAndStartsExpanded()
    {
        RegisterModule(
            [new NavGroup("Conference Day", Icon: EventIcon, Expanded: true)],
            Item("Check In", NavSection.Admin, "Conference Day"),
            Item("Rooms", NavSection.Admin, "Setup"));

        var cut = RenderSignedIn();

        var conferenceDay = GroupTitled(cut, "Conference Day");
        IsExpanded(conferenceDay).Should().BeTrue();
        GroupIcon(conferenceDay).Should().Be(IconPath(EventIcon));

        var setup = GroupTitled(cut, "Setup");
        IsExpanded(setup).Should().BeFalse("only the declared group changes");
        GroupIcon(setup).Should().Be(IconPath(Folder));
    }

    [Fact]
    public void GroupOrder_SortsGroupsWithinASection_TiesKeepFirstAppearance()
    {
        RegisterModule(
            [new NavGroup("Setup", Order: 2), new NavGroup("Reports", Order: 1), new NavGroup("Daily", Order: 1)],
            Item("Rooms", NavSection.Admin, "Setup"),
            Item("Users", NavSection.Admin),
            Item("Attendance", NavSection.Admin, "Reports"),
            Item("Check In", NavSection.Admin, "Daily"));

        var cut = RenderSignedIn();

        Outline(cut).SkipWhile(entry => entry != "#Administration").Should().Equal(
            "#Administration",
            "Users",
            "[Reports]",
            "Reports/Attendance",
            "[Daily]",
            "Daily/Check In",
            "[Setup]",
            "Setup/Rooms");
    }

    [Fact]
    public void AGroupDeclaredByAnotherModule_AppliesToTheItemsThatUseItsKey_FirstDeclarationWins()
    {
        RegisterModule([], Item("Push Notifications", NavSection.Admin, "Notifications"));
        RegisterModule([new NavGroup("Notifications", Icon: EventIcon, Expanded: true)]);
        RegisterModule([new NavGroup("Notifications", Icon: StarIcon, Expanded: false)]);

        var cut = RenderSignedIn();

        var notifications = GroupTitled(cut, "Notifications");
        IsExpanded(notifications).Should().BeTrue("the first registered declaration wins");
        GroupIcon(notifications).Should().Be(IconPath(EventIcon), "the first registered declaration wins");
    }

    [Fact]
    public void ItemOrder_SortsWithinAGroupAndWithinTheUngroupedItems_TiesKeepRegistrationOrder()
    {
        RegisterModule(
            [],
            Item("Sessions", NavSection.Admin, "Program") with { Order = 2 },
            Item("Profile", NavSection.User) with { Order = 9 },
            Item("Events", NavSection.Admin, "Program") with { Order = 1 });
        RegisterModule(
            [],
            Item("Happening Now", NavSection.User) with { Order = 1 },
            Item("Speakers", NavSection.Admin, "Program") with { Order = 2 },
            Item("Points", NavSection.User) with { Order = 1 });

        var cut = RenderSignedIn();

        Outline(cut).Should().Equal(
            "Home",
            "#My Account",
            "Happening Now",
            "Points",
            "Profile",
            "#Administration",
            "[Program]",
            "Program/Events",
            "Program/Sessions",
            "Program/Speakers");
    }

    private static NavItem Item(string title, NavSection section = NavSection.General, string? group = null)
        => new(title, "/" + Uri.EscapeDataString(title), Icons.Material.Filled.Circle, typeof(SharedResource), Section: section, Group: group);

    // One entry per rendered element of the main navigation list, in document order: "#Label" for a
    // section heading, "[Key]" for a group, "Key/Title" for a link inside a group, "Title" otherwise.
    private static List<string> Outline(IRenderedComponent<NavMenu> cut)
    {
        var navigationList = cut.Find("#nav-menu").Children.Single(e => e.ClassList.Contains("mud-navmenu"));
        var entries = new List<string>();
        foreach (var element in navigationList.QuerySelectorAll("*"))
        {
            if (element.ClassList.Contains("nav-section-label"))
            {
                entries.Add("#" + element.TextContent.Trim());
            }
            else if (element.ClassList.Contains("mud-nav-group"))
            {
                entries.Add($"[{GroupTitle(element)}]");
            }
            else if (element.LocalName == "a" && element.ClassList.Contains("mud-nav-link"))
            {
                var title = element.TextContent.Trim();
                var group = element.Closest(".mud-nav-group");
                entries.Add(group is null ? title : $"{GroupTitle(group)}/{title}");
            }
        }

        return entries;
    }

    private static string GroupTitle(IElement group) =>
        group.QuerySelector(".mud-nav-link-text")!.TextContent.Trim();

    private static IElement GroupTitled(IRenderedComponent<NavMenu> cut, string title) =>
        cut.FindAll(".mud-nav-group").Single(group => GroupTitle(group) == title);

    private static bool IsExpanded(IElement group) =>
        group.QuerySelector("[aria-expanded]")!.GetAttribute("aria-expanded") == "true";

    // The rendered SVG serializes each path as <path ...></path> while the icon constants use the
    // self-closing form, so icons are compared by their drawing data rather than as markup.
    private static string GroupIcon(IElement group)
    {
        var paths = group.QuerySelector(".mud-nav-link-icon")!.QuerySelectorAll("path");
        return paths[^1].GetAttribute("d")!;
    }

    private static string IconPath(string icon)
    {
        var start = icon.LastIndexOf("d=\"", StringComparison.Ordinal) + 3;
        return icon[start..icon.IndexOf('"', start)];
    }

    private IRenderedComponent<NavMenu> RenderSignedIn()
    {
        RenderMudProviders();
        return RenderAs<NavMenu>(TestPrincipal.AuthenticatedUser(), _ => { });
    }

    private void RegisterModule(IReadOnlyList<NavGroup> navGroups, params NavItem[] navItems)
        => Services.AddSingleton<IUIModule>(new StubUiModule(navItems, navGroups));

    private sealed class StubUiModule(IReadOnlyList<NavItem> navItems, IReadOnlyList<NavGroup> navGroups) : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = navItems;

        public IReadOnlyList<NavGroup> NavGroups { get; } = navGroups;

        public Assembly Assembly => typeof(StubUiModule).Assembly;
    }
}
