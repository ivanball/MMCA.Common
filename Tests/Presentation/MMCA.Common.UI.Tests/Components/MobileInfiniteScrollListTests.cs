using AwesomeAssertions;
using Bunit;
using MMCA.Common.UI.Components;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components;

public sealed class MobileInfiniteScrollListTests : BunitTestBase
{
    private static Func<int, int, CancellationToken, Task<(IReadOnlyList<string> Items, int TotalItems)>> Fetch(
        IReadOnlyList<string> page, int total)
        => (_, _, _) => Task.FromResult((page, total));

    [Fact]
    public void RendersEmptyState_WhenFetchReturnsNothing()
    {
        var cut = RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.FetchPage, Fetch([], 0))
            .Add(c => c.EmptyMessage, "Nothing here"));

        cut.Markup.Should().Contain("Nothing here");
        cut.FindComponents<MudCard>().Should().BeEmpty();
    }

    [Fact]
    public void RendersCardPerItem()
    {
        var items = new List<string> { "Alpha", "Bravo" };

        var cut = RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.FetchPage, Fetch(items, items.Count)));

        cut.Markup.Should().Contain("Alpha").And.Contain("Bravo");
        cut.FindComponents<MudCard>().Count.Should().Be(2);
    }

    [Fact]
    public void StopsLoading_WhenMaxRenderedItemsReached()
    {
        // The first page already fills the cap (2 items, cap 2) even though more exist
        // (total 10), so no infinite-scroll sentinel is rendered and loading stops.
        var items = new List<string> { "a", "b" };

        var cut = RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.PageSize, 2)
            .Add(c => c.MaxRenderedItems, 2)
            .Add(c => c.FetchPage, Fetch(items, 10)));

        cut.FindComponents<MudCard>().Count.Should().Be(2);
        cut.FindAll(".infinite-scroll-sentinel").Should().BeEmpty();
    }
}
