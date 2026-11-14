using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// The phone-list fetch helper and the before-fetch hook of <see cref="DataGridListPageBase{TDto}"/>:
/// <c>MobilePageFetcher</c> turns the grid's fetch delegate plus its filter callback into the
/// <c>MobileInfiniteScrollList.FetchPageResult</c> shape (a fresh ordinal filter bag per call, the
/// page arguments and default sort passed through, the Result handed back untouched, the optional
/// after-fetch step on success only), and <c>OnBeforeFetchAsync</c> runs ahead of the filter
/// callback on every loader (paged, virtualized, mobile and the phone-list delegate), inside the
/// load's failure and cancel handling, and never on the persisted-prerender shortcut.
/// </summary>
public sealed class DataGridListPageBaseMobileFetchTests : BunitTestBase
{
    private static readonly string PersistKey = $"grid:{typeof(TestGridPage).FullName}";

    private readonly Mock<IToastService> _toast = new();

    private readonly BunitPersistentComponentState _state;

    public DataGridListPageBaseMobileFetchTests()
    {
        // Last registration wins over the Mud-backed facade the base registered.
        Services.AddSingleton<IToastService>(_toast.Object);

        // The ConfigureDataGridListPageHost block, spelled out because the prerender-shortcut test
        // drives the persistent state double that helper creates and discards. SetRendererInfo stays
        // LAST: it freezes the service provider.
        Services.TryAddScoped<ListPageStateService>();
        Services.TryAddScoped<ListPageQueryStateService>();
        Services.AddSingleton(Mock.Of<IBrowserViewportService>());
        _state = AddBunitPersistentComponentState();
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    // Public so the persisted prerender page can round-trip through JSON.
    public sealed record WidgetRow(int Id, string Name);

    /// <summary>One call the fetch delegate saw. The filter bag is kept by reference, not copied.</summary>
    private sealed record FetchCall(
        Dictionary<string, (string Operator, string Value)> Filters,
        int Page,
        int PageSize,
        string? SortColumn,
        string? SortDirection,
        CancellationToken Token);

    private static Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)> Success(int totalItems, params WidgetRow[] items) =>
        Result.Success<(IReadOnlyList<WidgetRow> Items, int TotalItems)>((items, totalItems));

    private static Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)> Failure(string message) =>
        Result.Failure<(IReadOnlyList<WidgetRow> Items, int TotalItems)>(Error.Failure("Widgets.LoadFailed", message));

    /// <summary>A fetch delegate that records every call and answers <paramref name="answer"/> (an empty success by default).</summary>
    private static Func<Dictionary<string, (string Operator, string Value)>, int, int, string?, string?, CancellationToken, Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>>> Recording(
        List<FetchCall> calls,
        Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>? answer = null) =>
        (filters, page, pageSize, sortColumn, sortDirection, token) =>
        {
            calls.Add(new FetchCall(filters, page, pageSize, sortColumn, sortDirection, token));
            return Task.FromResult(answer ?? Success(0));
        };

    private static async Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>> FetchOnDispatcherAsync(
        IRenderedComponent<TestGridPage> cut,
        Func<int, int, CancellationToken, Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>>> fetcher,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>? result = null;
        await cut.InvokeAsync(async () =>
        {
            result = await fetcher(page, pageSize, cancellationToken);
        });
        return result!;
    }

    private static async Task<GridData<WidgetRow>> LoadOnDispatcherAsync(
        IRenderedComponent<TestGridPage> cut,
        GridState<WidgetRow> state,
        Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null,
        bool showCancelSnackbar = true)
    {
        GridData<WidgetRow>? data = null;
        await cut.InvokeAsync(async () =>
        {
            data = await cut.Instance.LoadAsync(state, additionalFilters, showCancelSnackbar);
        });
        return data!;
    }

    private static GridState<WidgetRow> State(int page = 0, int pageSize = 10) =>
        new() { Page = page, PageSize = pageSize };

    private static IFilterDefinition<WidgetRow> Filter(string propertyName, string @operator, string value)
    {
        var column = new Mock<Column<WidgetRow>>();
        column.Setup(c => c.PropertyName).Returns(propertyName);

        var definition = new Mock<IFilterDefinition<WidgetRow>>();
        definition.SetupGet(f => f.Column).Returns(column.Object);
        definition.SetupGet(f => f.Operator).Returns(@operator);
        definition.SetupGet(f => f.Value).Returns(value);
        return definition.Object;
    }

    // == MobilePageFetcher: argument validation and pass-through (rules 13.1-13.4) ==
    [Fact]
    public void MobilePageFetcher_NullFetch_Throws()
    {
        var cut = Render<TestGridPage>();

        // The throw belongs to the call that builds the delegate, not to its first invocation.
        var act = () => cut.Instance.CreateMobileFetcher(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task MobilePageFetcher_PassesPageAndSizeThrough()
    {
        var cut = Render<TestGridPage>();
        var calls = new List<FetchCall>();
        var fetcher = cut.Instance.CreateMobileFetcher(Recording(calls));

        await FetchOnDispatcherAsync(cut, fetcher, page: 3, pageSize: 25);

        calls.Should().ContainSingle();
        calls[0].Page.Should().Be(3);
        calls[0].PageSize.Should().Be(25);
    }

    [Fact]
    public async Task MobilePageFetcher_DefaultSortAscending_SendsColumnAndAsc()
    {
        var cut = Render<TestGridPage>();
        var calls = new List<FetchCall>();
        var fetcher = cut.Instance.CreateMobileFetcher(Recording(calls), defaultSortColumn: "Name");

        await FetchOnDispatcherAsync(cut, fetcher);

        calls.Should().ContainSingle();
        calls[0].SortColumn.Should().Be("Name");
        calls[0].SortDirection.Should().Be("asc");
    }

    [Fact]
    public async Task MobilePageFetcher_Descending_SendsDesc()
    {
        var cut = Render<TestGridPage>();
        var calls = new List<FetchCall>();
        var fetcher = cut.Instance.CreateMobileFetcher(
            Recording(calls),
            defaultSortColumn: "CreatedOn",
            defaultSortDirection: SortDirection.Descending);

        await FetchOnDispatcherAsync(cut, fetcher);

        calls.Should().ContainSingle();
        calls[0].SortColumn.Should().Be("CreatedOn");
        calls[0].SortDirection.Should().Be("desc");
    }

    [Fact]
    public async Task MobilePageFetcher_SortDirectionNone_SendsAsc()
    {
        // "asc" for every value other than Descending, None included.
        var cut = Render<TestGridPage>();
        var calls = new List<FetchCall>();
        var fetcher = cut.Instance.CreateMobileFetcher(
            Recording(calls),
            defaultSortColumn: "Name",
            defaultSortDirection: SortDirection.None);

        await FetchOnDispatcherAsync(cut, fetcher);

        calls.Should().ContainSingle();
        calls[0].SortColumn.Should().Be("Name");
        calls[0].SortDirection.Should().Be("asc");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MobilePageFetcher_NoDefaultSort_SendsNullColumnAndDirection(string? defaultSortColumn)
    {
        var cut = Render<TestGridPage>();
        var calls = new List<FetchCall>();
        var fetcher = cut.Instance.CreateMobileFetcher(
            Recording(calls),
            defaultSortColumn: defaultSortColumn,
            defaultSortDirection: SortDirection.Descending);

        await FetchOnDispatcherAsync(cut, fetcher);

        calls.Should().ContainSingle();
        calls[0].SortColumn.Should().BeNull();
        calls[0].SortDirection.Should().BeNull("no default sort column means no sort direction either, whatever the direction argument");
    }

    // == MobilePageFetcher: the filter bag (rule 13.2) ==
    [Fact]
    public async Task MobilePageFetcher_RunsAdditionalFiltersAtInvocationTime()
    {
        var cut = Render<TestGridPage>();
        var calls = new List<FetchCall>();
        var status = "open";
        var fetcher = cut.Instance.CreateMobileFetcher(
            Recording(calls),
            filters => filters["Status"] = ("eq", status));

        await FetchOnDispatcherAsync(cut, fetcher);
        status = "closed";
        await FetchOnDispatcherAsync(cut, fetcher);

        calls.Should().HaveCount(2);
        calls[0].Filters.Should().ContainKey("Status").WhoseValue.Should().Be(("eq", "open"));
        calls[1].Filters.Should().ContainKey("Status").WhoseValue.Should().Be(("eq", "closed"),
            "the callback runs when the delegate is invoked, not when it is built");
        calls[1].Filters.Should().NotBeSameAs(calls[0].Filters, "every invocation builds a new bag");
    }

    [Fact]
    public async Task MobilePageFetcher_BagHasOnlyCallbackFilters_OrdinalComparer()
    {
        var cut = Render<TestGridPage>();

        // A grid load with a column filter first, so the page holds grid filter state the phone
        // list must never pick up.
        var gridState = State();
        gridState.FilterDefinitions = [Filter("Name", "contains", "blue")];
        await LoadOnDispatcherAsync(cut, gridState);

        var calls = new List<FetchCall>();
        var fetcher = cut.Instance.CreateMobileFetcher(
            Recording(calls),
            filters => filters["search"] = ("contains", "smith"));

        await FetchOnDispatcherAsync(cut, fetcher);

        calls.Should().ContainSingle();
        var bag = calls[0].Filters;
        bag.Keys.Should().Equal(["search"], "only the callback's filters travel, never a grid column filter");
        bag["search"].Should().Be(("contains", "smith"));
        bag.Comparer.Should().BeSameAs(StringComparer.Ordinal);
    }

    [Fact]
    public async Task MobilePageFetcher_WithoutAdditionalFilters_SendsAnEmptyBag()
    {
        var cut = Render<TestGridPage>();
        var calls = new List<FetchCall>();
        var fetcher = cut.Instance.CreateMobileFetcher(Recording(calls));

        await FetchOnDispatcherAsync(cut, fetcher);

        calls.Should().ContainSingle();
        calls[0].Filters.Should().BeEmpty();
    }

    // == MobilePageFetcher: the Result and the page state (rule 13.5) ==
    [Fact]
    public async Task MobilePageFetcher_Failure_ReturnedUnchanged_NoToast_LoadFailedFalse()
    {
        var cut = Render<TestGridPage>();
        var navigation = Services.GetRequiredService<NavigationManager>();
        var uriBefore = navigation.Uri;
        var failed = Failure("The widget service is unavailable.");
        var fetcher = cut.Instance.CreateMobileFetcher((_, _, _, _, _, _) => Task.FromResult(failed));

        var result = await FetchOnDispatcherAsync(cut, fetcher);

        result.Should().BeSameAs(failed, "the list owns retry and error display, so the Result goes back untouched");
        _toast.VerifyNoOtherCalls();
        cut.Instance.LoadFailedNow.Should().BeFalse();
        cut.Instance.LoadingNow.Should().BeFalse();
        cut.Instance.EmptyConfirmedNow.Should().BeFalse();
        cut.Instance.MobileItemsNow.Should().BeEmpty();
        navigation.Uri.Should().Be(uriBefore, "the delegate saves no list state, so nothing is mirrored to the URL");
    }

    [Fact]
    public async Task MobilePageFetcher_Success_ReturnedUnchanged_LeavesPageStateAlone()
    {
        var cut = Render<TestGridPage>();
        var navigation = Services.GetRequiredService<NavigationManager>();
        var uriBefore = navigation.Uri;
        var page = Success(0);
        var fetcher = cut.Instance.CreateMobileFetcher((_, _, _, _, _, _) => Task.FromResult(page), defaultSortColumn: "Name");

        var result = await FetchOnDispatcherAsync(cut, fetcher);

        result.Should().BeSameAs(page);
        _toast.VerifyNoOtherCalls();
        cut.Instance.LoadingNow.Should().BeFalse();
        cut.Instance.LoadFailedNow.Should().BeFalse();
        cut.Instance.EmptyConfirmedNow.Should().BeFalse("an empty phone-list page is the list's to show, not the grid's empty state");
        cut.Instance.MobileItemsNow.Should().BeEmpty();
        navigation.Uri.Should().Be(uriBefore);
    }

    // == MobilePageFetcher: after-fetch step (rule 13.7) ==
    [Fact]
    public async Task MobilePageFetcher_Success_RunsAfterFetchWithItemsAndToken()
    {
        var cut = Render<TestGridPage>();
        var items = new[] { new WidgetRow(1, "First"), new WidgetRow(2, "Second") };
        var steps = new List<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<WidgetRow>? seenItems = null;
        CancellationToken seenToken = default;

        var fetcher = cut.Instance.CreateMobileFetcher(
            (_, _, _, _, _, _) =>
            {
                steps.Add("fetch");
                return Task.FromResult(Success(2, items));
            },
            afterFetchAsync: async (pageItems, token) =>
            {
                steps.Add("after");
                seenItems = pageItems;
                seenToken = token;
                await gate.Task;
            });

        using var cts = new CancellationTokenSource();
        Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>>? pending = null;
        await cut.InvokeAsync(() =>
        {
            pending = fetcher(1, 10, cts.Token);
        });

        await cut.WaitForAssertionAsync(() => steps.Should().Equal("fetch", "after"));
        pending!.IsCompleted.Should().BeFalse("the delegate awaits the after-fetch step before it returns");

        gate.SetResult();
        var result = await pending!;

        result.IsSuccess.Should().BeTrue();
        seenItems.Should().Equal(items);
        seenToken.Should().Be(cts.Token, "the after-fetch step gets the delegate's own token");
    }

    [Fact]
    public async Task MobilePageFetcher_Failure_DoesNotRunAfterFetch()
    {
        var cut = Render<TestGridPage>();
        var afterFetchRuns = 0;
        var fetcher = cut.Instance.CreateMobileFetcher(
            (_, _, _, _, _, _) => Task.FromResult(Failure("down")),
            afterFetchAsync: (_, _) =>
            {
                afterFetchRuns++;
                return Task.CompletedTask;
            });

        var result = await FetchOnDispatcherAsync(cut, fetcher);

        result.IsFailure.Should().BeTrue();
        afterFetchRuns.Should().Be(0);
    }

    // == MobilePageFetcher: the before-fetch hook (rule 13.6) ==
    [Fact]
    public async Task MobilePageFetcher_AwaitsBeforeFetchBeforeFilters()
    {
        var cut = Render<TestGridPage>();
        string? eventId = null;
        cut.Instance.BeforeFetch = async _ =>
        {
            await Task.Yield();
            eventId = "42";
        };

        var calls = new List<FetchCall>();
        var fetcher = cut.Instance.CreateMobileFetcher(
            Recording(calls),
            filters =>
            {
                if (eventId is not null)
                {
                    filters["EventId"] = ("eq", eventId);
                }
            });

        await FetchOnDispatcherAsync(cut, fetcher);

        calls.Should().ContainSingle();
        calls[0].Filters.Should().ContainKey("EventId").WhoseValue.Should().Be(("eq", "42"),
            "the hook completes before the filter callback reads what it set");
    }

    [Fact]
    public async Task MobilePageFetcher_PassesItsOwnTokenToBeforeFetch()
    {
        var cut = Render<TestGridPage>();
        var calls = new List<FetchCall>();
        var fetcher = cut.Instance.CreateMobileFetcher(Recording(calls));
        using var cts = new CancellationTokenSource();

        await FetchOnDispatcherAsync(cut, fetcher, cancellationToken: cts.Token);

        cut.Instance.BeforeFetchTokens.Should().ContainSingle().Which.Should().Be(cts.Token);
    }

    // == MobilePageFetcher: exceptions (rule 13.8) ==
    [Fact]
    public async Task MobilePageFetcher_FetchThrows_ExceptionPropagates()
    {
        var cut = Render<TestGridPage>();
        var boom = new InvalidOperationException("backend down");
        var fetcher = cut.Instance.CreateMobileFetcher((_, _, _, _, _, _) => throw boom);

        var act = () => FetchOnDispatcherAsync(cut, fetcher);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        _toast.VerifyNoOtherCalls();
        cut.Instance.LoadFailedNow.Should().BeFalse("the list, not the page, reports a throwing fetch");
    }

    [Theory]
    [InlineData("hook")]
    [InlineData("filters")]
    [InlineData("after")]
    public async Task MobilePageFetcher_HookFiltersOrAfterFetchThrow_ExceptionPropagates(string thrower)
    {
        var cut = Render<TestGridPage>();
        var boom = new InvalidOperationException(thrower);
        if (string.Equals(thrower, "hook", StringComparison.Ordinal))
        {
            cut.Instance.BeforeFetch = _ => throw boom;
        }

        var fetcher = cut.Instance.CreateMobileFetcher(
            (_, _, _, _, _, _) => Task.FromResult(Success(1, new WidgetRow(1, "First"))),
            _ =>
            {
                if (string.Equals(thrower, "filters", StringComparison.Ordinal))
                {
                    throw boom;
                }
            },
            afterFetchAsync: (_, _) => string.Equals(thrower, "after", StringComparison.Ordinal) ? throw boom : Task.CompletedTask);

        var act = () => FetchOnDispatcherAsync(cut, fetcher);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        _toast.VerifyNoOtherCalls();
    }

    // == Grid loaders await the hook (rule 13.9) ==
    [Fact]
    public async Task GridLoad_AwaitsBeforeFetchBeforeFilters()
    {
        var cut = Render<TestGridPage>();
        string? eventId = null;
        cut.Instance.BeforeFetch = async _ =>
        {
            await Task.Yield();
            eventId = "42";
        };
        var calls = new List<FetchCall>();
        cut.Instance.Fetch = Recording(calls);

        await LoadOnDispatcherAsync(
            cut,
            State(),
            filters =>
            {
                if (eventId is not null)
                {
                    filters["EventId"] = ("eq", eventId);
                }
            });

        calls.Should().ContainSingle();
        calls[0].Filters.Should().ContainKey("EventId").WhoseValue.Should().Be(("eq", "42"));
        cut.Instance.BeforeFetchTokens.Should().ContainSingle().Which.Should().Be(calls[0].Token,
            "the hook gets the same bounded fetch token the fetch does");
    }

    [Fact]
    public async Task VirtualizedLoad_AwaitsBeforeFetch()
    {
        var cut = Render<TestGridPage>();
        string? eventId = null;
        cut.Instance.BeforeFetch = async _ =>
        {
            await Task.Yield();
            eventId = "42";
        };
        var calls = new List<FetchCall>();
        cut.Instance.Fetch = Recording(calls);

        await cut.InvokeAsync(async () =>
        {
            await cut.Instance.LoadVirtualizedAsync(
                new GridStateVirtualize<WidgetRow> { StartIndex = 0, Count = 10 },
                filters =>
                {
                    if (eventId is not null)
                    {
                        filters["EventId"] = ("eq", eventId);
                    }
                });
        });

        calls.Should().ContainSingle();
        calls[0].Filters.Should().ContainKey("EventId").WhoseValue.Should().Be(("eq", "42"));
        cut.Instance.BeforeFetchTokens.Should().ContainSingle().Which.Should().Be(calls[0].Token);
    }

    [Fact]
    public async Task LoadMobileDataAsync_AwaitsBeforeFetch()
    {
        var cut = Render<TestGridPage>();
        string? eventId = null;
        cut.Instance.BeforeFetch = async _ =>
        {
            await Task.Yield();
            eventId = "42";
        };
        var calls = new List<FetchCall>();
        cut.Instance.Fetch = Recording(calls);

        await cut.InvokeAsync(async () => await cut.Instance.LoadMobileAsync(filters =>
        {
            if (eventId is not null)
            {
                filters["EventId"] = ("eq", eventId);
            }
        }));

        calls.Should().ContainSingle();
        calls[0].Filters.Should().ContainKey("EventId").WhoseValue.Should().Be(("eq", "42"));
        cut.Instance.BeforeFetchTokens.Should().ContainSingle().Which.Should().Be(calls[0].Token);
    }

    [Fact]
    public async Task GridLoad_SupersededWhileBeforeFetchRuns_DoesNotFetchForTheSupersededLoad()
    {
        // The hook of the first load ignores its token and finishes after a newer load took over;
        // the superseded load must stop before it builds filters or fetches.
        var cut = Render<TestGridPage>();
        var firstHookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookCalls = 0;
        cut.Instance.BeforeFetch = async _ =>
        {
            if (++hookCalls == 1)
            {
                firstHookEntered.SetResult();
                await releaseFirstHook.Task;
            }
        };
        var calls = new List<FetchCall>();
        cut.Instance.Fetch = Recording(calls, Success(1, new WidgetRow(4, "Newer")));
        var filterRuns = 0;

        Task<GridData<WidgetRow>>? superseded = null;
        await cut.InvokeAsync(() =>
        {
            superseded = cut.Instance.LoadAsync(State(), _ => filterRuns++, showCancelSnackbar: false);
        });
        await firstHookEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var newer = await LoadOnDispatcherAsync(cut, State(page: 1), _ => filterRuns++, showCancelSnackbar: false);
        newer.Items.Should().ContainSingle();
        calls.Should().ContainSingle("only the newest load has fetched so far");

        releaseFirstHook.SetResult();
        await superseded!;

        calls.Should().ContainSingle("the superseded load stops after its hook, before it fetches");
        filterRuns.Should().Be(1, "the superseded load never reaches its filter callback");
        _toast.VerifyNoOtherCalls();
    }

    // == A throwing or cancelled hook inside a grid loader (rule 13.10) ==
    [Fact]
    public async Task GridLoad_BeforeFetchThrows_ToastsErrorAndSetsLoadFailed()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.BeforeFetch = _ => throw new InvalidOperationException("events did not load");
        var calls = new List<FetchCall>();
        cut.Instance.Fetch = Recording(calls);

        var data = await LoadOnDispatcherAsync(cut, State());

        data.Items.Should().BeEmpty();
        data.TotalItems.Should().Be(0);
        calls.Should().BeEmpty();
        cut.Instance.LoadFailedNow.Should().BeTrue();
        cut.Instance.LoadingNow.Should().BeFalse("the loading flag must always be reset");
        _toast.Verify(t => t.Error(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task GridLoad_BeforeFetchCanceled_FollowsTheCancelPath()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.BeforeFetch = _ => throw new OperationCanceledException();
        var calls = new List<FetchCall>();
        cut.Instance.Fetch = Recording(calls);

        var data = await LoadOnDispatcherAsync(cut, State(), showCancelSnackbar: true);

        data.Items.Should().BeEmpty();
        calls.Should().BeEmpty();
        cut.Instance.LoadFailedNow.Should().BeFalse("a cancel is not a failure");
        _toast.Verify(t => t.Info(It.IsAny<string>()), Times.Once);
        _toast.Verify(t => t.Error(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GridLoad_BeforeFetchCanceled_WithToastSuppressed_StaysQuiet()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.BeforeFetch = _ => throw new OperationCanceledException();

        await LoadOnDispatcherAsync(cut, State(), showCancelSnackbar: false);

        cut.Instance.LoadFailedNow.Should().BeFalse();
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GridLoad_CancelLoading_CancelsTheTokenTheHookReceived()
    {
        var cut = Render<TestGridPage>();
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cut.Instance.BeforeFetch = async token =>
        {
            hookEntered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var calls = new List<FetchCall>();
        cut.Instance.Fetch = Recording(calls);

        Task<GridData<WidgetRow>>? load = null;
        await cut.InvokeAsync(() =>
        {
            load = cut.Instance.LoadAsync(State());
        });
        await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(cut.Instance.CancelLoading);
        var data = await load!.WaitAsync(TimeSpan.FromSeconds(5));

        cut.Instance.BeforeFetchTokens.Should().ContainSingle().Which.IsCancellationRequested.Should().BeTrue();
        data.Items.Should().BeEmpty();
        calls.Should().BeEmpty();
        cut.Instance.LoadingNow.Should().BeFalse();
    }

    // == The persisted-prerender shortcut skips the hook (rule 13.11) ==
    [Fact]
    public async Task GridLoad_PersistedPrerenderData_DoesNotRunBeforeFetch()
    {
        _state.Persist(PersistKey, new { Items = new[] { new WidgetRow(7, "Restored") }, TotalItems = 42, Page = 0, PageSize = 10 });
        var cut = Render<TestGridPage>();
        var calls = new List<FetchCall>();
        cut.Instance.Fetch = Recording(calls);

        var data = await LoadOnDispatcherAsync(cut, State());

        data.TotalItems.Should().Be(42, "the restored prerender page answers the first interactive load");
        calls.Should().BeEmpty();
        cut.Instance.BeforeFetchTokens.Should().BeEmpty("the shortcut returns before the hook");
    }

    // == A page that adopts nothing (rule 13.12) ==
    [Fact]
    public async Task DefaultBeforeFetch_IsCompleted_AndAPlainPageFetchesOnceAsToday()
    {
        var cut = Render<PlainGridPage>();
        var calls = new List<FetchCall>();
        cut.Instance.Fetch = Recording(calls);

        cut.Instance.CallDefaultBeforeFetch().IsCompletedSuccessfully.Should().BeTrue("the default hook is Task.CompletedTask");

        await cut.InvokeAsync(async () =>
        {
            await cut.Instance.LoadAsync(State(page: 2, pageSize: 25), filters => filters["search"] = ("contains", "blue"));
        });

        calls.Should().ContainSingle();
        calls[0].Page.Should().Be(3);
        calls[0].PageSize.Should().Be(25);
        calls[0].Filters.Keys.Should().Equal(["search"], "the grid filter bag carries the callback filter as it does today");
        calls[0].SortColumn.Should().BeNull();
        calls[0].SortDirection.Should().Be("asc", "a grid load with no sort definition sends asc today, and rule 12 keeps that request unchanged");
    }

    private sealed class TestGridPage : DataGridListPageBase<WidgetRow>
    {
        public Func<Dictionary<string, (string Operator, string Value)>, int, int, string?, string?, CancellationToken, Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>>> Fetch { get; set; } =
            (_, _, _, _, _, _) => Task.FromResult(Success(0));

        /// <summary>What the overridden hook does after recording its token; null does nothing.</summary>
        public Func<CancellationToken, Task>? BeforeFetch { get; set; }

        /// <summary>Every token <c>OnBeforeFetchAsync</c> received, in call order.</summary>
        public List<CancellationToken> BeforeFetchTokens { get; } = [];

        public bool LoadingNow => IsLoading;

        public bool LoadFailedNow => LoadFailed;

        public bool EmptyConfirmedNow => EmptyConfirmed;

        public IReadOnlyList<WidgetRow> MobileItemsNow => MobileItems;

        protected override string Title => "Widgets";

        public Func<int, int, CancellationToken, Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>>> CreateMobileFetcher(
            Func<Dictionary<string, (string Operator, string Value)>, int, int, string?, string?, CancellationToken, Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>>> fetchAsync,
            Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null,
            string? defaultSortColumn = null,
            SortDirection defaultSortDirection = SortDirection.Ascending,
            Func<IReadOnlyList<WidgetRow>, CancellationToken, Task>? afterFetchAsync = null) =>
            MobilePageFetcher(fetchAsync, additionalFilters, defaultSortColumn, defaultSortDirection, afterFetchAsync);

        public Task<GridData<WidgetRow>> LoadAsync(
            GridState<WidgetRow> state,
            Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null,
            bool showCancelSnackbar = true) =>
            LoadServerDataAsync(state, Fetch, additionalFilters, showCancelSnackbar);

        public Task<GridData<WidgetRow>> LoadVirtualizedAsync(
            GridStateVirtualize<WidgetRow> state,
            Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null) =>
            LoadVirtualizedServerDataAsync(state, Fetch, additionalFilters);

        public Task LoadMobileAsync(Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null) =>
            LoadMobileDataAsync(Fetch, additionalFilters);

        protected override async Task OnBeforeFetchAsync(CancellationToken cancellationToken)
        {
            BeforeFetchTokens.Add(cancellationToken);
            if (BeforeFetch is not null)
            {
                await BeforeFetch(cancellationToken);
            }
        }
    }

    /// <summary>A page that neither overrides the hook nor uses the phone-list helper.</summary>
    private sealed class PlainGridPage : DataGridListPageBase<WidgetRow>
    {
        public Func<Dictionary<string, (string Operator, string Value)>, int, int, string?, string?, CancellationToken, Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>>> Fetch { get; set; } =
            (_, _, _, _, _, _) => Task.FromResult(Success(0));

        protected override string Title => "Widgets";

        public Task CallDefaultBeforeFetch() => OnBeforeFetchAsync(CancellationToken.None);

        public Task<GridData<WidgetRow>> LoadAsync(
            GridState<WidgetRow> state,
            Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null) =>
            LoadServerDataAsync(state, Fetch, additionalFilters);
    }
}
