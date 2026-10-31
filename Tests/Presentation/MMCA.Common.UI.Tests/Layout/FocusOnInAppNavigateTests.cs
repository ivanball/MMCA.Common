using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Layout;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// <see cref="FocusOnInAppNavigate"/> mounts <see cref="FocusOnNavigate"/> (which focuses the h1 on its
/// first render) only on the first in-app navigation to another page. A list page mirroring its grid
/// state into the URL right after its first data load keeps the path and rewrites only the query, so
/// it must not count: focus on a freshly loaded page stays at the document start for the skip link.
/// </summary>
public sealed class FocusOnInAppNavigateTests : BunitTestBase
{
    private static readonly RouteData PageRoute = new(typeof(ListPage), new Dictionary<string, object?>());

    [Fact]
    public void QueryStringRewriteOnTheSamePage_DoesNotMountFocusOnNavigate()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/products");
        var cut = RenderUnderTest<FocusOnInAppNavigate>(p => p.Add(c => c.RouteData, PageRoute));

        navigation.NavigateTo("/products?ps=10&sort=name", new NavigationOptions { ReplaceHistoryEntry = true });

        cut.FindComponents<FocusOnNavigate>().Should().BeEmpty("a query-only rewrite is not a navigation to another page");
    }

    [Fact]
    public void NavigationToAnotherPath_MountsFocusOnNavigate()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/products");
        var cut = RenderUnderTest<FocusOnInAppNavigate>(p => p.Add(c => c.RouteData, PageRoute));

        navigation.NavigateTo("/products?ps=10", new NavigationOptions { ReplaceHistoryEntry = true });
        navigation.NavigateTo("/products/1");

        cut.WaitForAssertion(() => cut.FindComponents<FocusOnNavigate>().Should().ContainSingle());
    }

    private sealed class ListPage : ComponentBase;
}
