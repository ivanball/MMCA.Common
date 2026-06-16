using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MMCA.Common.Testing.UI;
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

    [Fact]
    public void ClickingCard_InvokesOnCardClickWithItem()
    {
        string? clicked = null;

        var cut = RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.FetchPage, Fetch(["Alpha", "Bravo"], 2))
            .Add(c => c.OnCardClick, EventCallback.Factory.Create<string>(this, s => clicked = s)));

        cut.FindAll(".mobile-list-card")[0].Click();

        clicked.Should().Be("Alpha");
    }

    [Fact]
    public async Task WhenLoadMoreFails_ShowsRetry_ThenRecoversOnRetry()
    {
        var page2Attempts = 0;

        Task<(IReadOnlyList<string> Items, int TotalItems)> Fetch(int page, int pageSize, CancellationToken ct)
        {
            if (page == 1)
            {
                return Task.FromResult<(IReadOnlyList<string>, int)>((["Alpha"], 5));
            }

            // First attempt at the second page fails transiently; the retry succeeds.
            page2Attempts++;
            return page2Attempts == 1
                ? throw new InvalidOperationException("transient")
                : Task.FromResult<(IReadOnlyList<string>, int)>((["Bravo"], 5));
        }

        var cut = RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.PageSize, 1)
            .Add(c => c.FetchPage, Fetch));

        cut.Markup.Should().Contain("Alpha");

        // Simulate the IntersectionObserver firing for the bottom sentinel.
        await cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());
        cut.Markup.Should().Contain("Failed to load more items.");

        await cut.FindButtonByText("Retry").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Bravo"));
    }
}
