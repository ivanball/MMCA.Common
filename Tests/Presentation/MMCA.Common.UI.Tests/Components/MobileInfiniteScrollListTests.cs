using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components;

public sealed class MobileInfiniteScrollListTests : BunitTestBase
{
    private static Func<int, int, CancellationToken, Task<Result<(IReadOnlyList<string> Items, int TotalItems)>>> Fetch(
        IReadOnlyList<string> page, int total)
        => (_, _, _) => Task.FromResult(Result.Success<(IReadOnlyList<string>, int)>((page, total)));

    [Fact]
    public void RendersEmptyState_WhenFetchReturnsNothing()
    {
        var cut = RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.FetchPageResult, Fetch([], 0))
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
            .Add(c => c.FetchPageResult, Fetch(items, items.Count)));

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
            .Add(c => c.FetchPageResult, Fetch(items, 10)));

        cut.FindComponents<MudCard>().Count.Should().Be(2);
        cut.FindAll(".infinite-scroll-sentinel").Should().BeEmpty();
    }

    [Fact]
    public void ClickingCard_InvokesOnCardClickWithItem()
    {
        string? clicked = null;

        var cut = RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.FetchPageResult, Fetch(["Alpha", "Bravo"], 2))
            .Add(c => c.OnCardClick, EventCallback.Factory.Create<string>(this, s => clicked = s)));

        cut.FindAll(".mobile-list-card")[0].Click();

        clicked.Should().Be("Alpha");
    }

    [Fact]
    public void SupplyingNeitherFetcher_Throws()
    {
        var render = () => RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item));

        render.Should().Throw<InvalidOperationException>().WithMessage("*exactly one*");
    }

    [Fact]
    public async Task WhenLoadMoreFails_ShowsLocalizedResultError_ThenRecoversOnRetry()
    {
        var page2Attempts = 0;

        Task<Result<(IReadOnlyList<string> Items, int TotalItems)>> Fetch(int page, int pageSize, CancellationToken ct)
        {
            if (page == 1)
            {
                return Task.FromResult(Result.Success<(IReadOnlyList<string>, int)>((["Alpha"], 5)));
            }

            // First attempt at the second page fails as a Result (services no longer throw for a
            // server answer); the retry succeeds.
            page2Attempts++;
            return page2Attempts == 1
                ? Task.FromResult(Result.Failure<(IReadOnlyList<string>, int)>(
                    Error.NotFoundError("Catalog.Unavailable", "The catalog is unavailable.")))
                : Task.FromResult(Result.Success<(IReadOnlyList<string>, int)>((["Bravo"], 5)));
        }

        var cut = RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.PageSize, 1)
            .Add(c => c.FetchPageResult, Fetch));

        cut.Markup.Should().Contain("Alpha");

        // Simulate the IntersectionObserver firing for the bottom sentinel.
        await cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());

        // The failure's own message is shown (localized with pass-through), not the generic one.
        cut.Markup.Should().Contain("The catalog is unavailable.")
            .And.NotContain("Failed to load more items.");

        await cut.FindButtonByText("Retry").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Bravo"));
        cut.Markup.Should().NotContain("The catalog is unavailable.");
        page2Attempts.Should().Be(2, "the retry must re-fetch the page that failed");
    }

    [Fact]
    public async Task WhenFetchIsCancelled_RendersNoErrorState()
    {
        static Task<Result<(IReadOnlyList<string> Items, int TotalItems)>> Fetch(int page, int pageSize, CancellationToken ct)
            => page == 1
                ? Task.FromResult(Result.Success<(IReadOnlyList<string>, int)>((["Alpha"], 5)))
                : throw new OperationCanceledException();

        var cut = RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.PageSize, 1)
            .Add(c => c.FetchPageResult, Fetch));

        await cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());

        // Cancellation is not a failure: no message, no retry button, nothing to undo.
        cut.Markup.Should().NotContain("Failed to load more items.");
        cut.FindAll("button").Should().BeEmpty();
        cut.FindComponents<MudCard>().Count.Should().Be(1);
    }

    [Fact]
    public void ObsoleteTupleFetch_RendersCards()
    {
        var items = new List<string> { "Alpha", "Bravo" };

        var cut = RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
#pragma warning disable CS0618 // The obsolete tuple path must keep working until every call site is swept.
            .Add(c => c.FetchPage, (_, _, _) => Task.FromResult<(IReadOnlyList<string>, int)>((items, items.Count))));
#pragma warning restore CS0618

        cut.Markup.Should().Contain("Alpha").And.Contain("Bravo");
        cut.FindComponents<MudCard>().Count.Should().Be(2);
    }

    [Fact]
    public async Task ObsoleteTupleFetch_WhenLoadMoreThrows_ShowsRetry_ThenRecoversOnRetry()
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
#pragma warning disable CS0618 // The obsolete tuple path must keep working until every call site is swept.
            .Add(c => c.FetchPage, Fetch));
#pragma warning restore CS0618

        cut.Markup.Should().Contain("Alpha");

        // Simulate the IntersectionObserver firing for the bottom sentinel.
        await cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());

        // An exception carries no message that is safe to surface, so the generic one is shown.
        cut.Markup.Should().Contain("Failed to load more items.");

        await cut.FindButtonByText("Retry").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Bravo"));
    }

    [Fact]
    public async Task ResetDuringAnInFlightFetch_DiscardsTheStalePage_AndReloadsWithoutRefetching()
    {
        var requestedPages = new List<int>();
        var stalePage = new TaskCompletionSource<Result<(IReadOnlyList<string> Items, int TotalItems)>>();

        Task<Result<(IReadOnlyList<string> Items, int TotalItems)>> GatedFetch(int page, int pageSize, CancellationToken ct)
        {
            requestedPages.Add(page);

            // Call 1 is the initial load. Call 2 (the sentinel's page 2) is held open so the reset
            // lands while it is still outstanding. Everything after that answers immediately.
            if (requestedPages.Count == 1)
            {
                return Task.FromResult(Result.Success<(IReadOnlyList<string>, int)>((["Original"], 3)));
            }

            if (requestedPages.Count == 2)
            {
                return stalePage.Task;
            }

            return page == 1
                ? Task.FromResult(Result.Success<(IReadOnlyList<string>, int)>((["FreshPageOne"], 3)))
                : Task.FromResult(Result.Success<(IReadOnlyList<string>, int)>((["FreshPageTwo"], 3)));
        }

        var cut = RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.PageSize, 1)
            .Add(c => c.FetchPageResult, GatedFetch));

        cut.Markup.Should().Contain("Original");

        // Drive the whole race inside a single dispatcher work item so every step is deterministic:
        // the sentinel's page-2 fetch suspends on the gate, the search/filter criteria change lands
        // on top of it, and only then does the superseded fetch answer.
        Task? supersededLoad = null;
        await cut.InvokeAsync(async () =>
        {
            supersededLoad = cut.Instance.OnSentinelVisible();

            await cut.Instance.ResetAsync();

            stalePage.SetResult(Result.Success<(IReadOnlyList<string>, int)>((["Stale"], 50)));
        });

        await supersededLoad!;

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(
            "FreshPageOne", "the reset must actually reload, not early-return on the in-flight flag"));
        cut.Markup.Should().NotContain("Stale", "a superseded fetch must not append into the list the reset cleared");
        cut.Markup.Should().NotContain("Original");
        cut.FindComponents<MudCard>().Count.Should().Be(1);

        // Initial load, the superseded page 2, then the reset's reload of page 1.
        requestedPages.Should().Equal(1, 2, 1);

        // The next sentinel hit must move forward instead of re-requesting the page the superseded
        // fetch already consumed (which would render a duplicate row).
        await cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("FreshPageTwo"));
        requestedPages.Should().Equal(1, 2, 1, 2);
        cut.FindComponents<MudCard>().Count.Should().Be(2);
    }
}
