using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// bUnit host tests for <see cref="DataGridListPageBase{TDto}"/> through a minimal concrete page:
/// initial server-data load, page/sort state persistence mirrored to the URL, the failed-fetch and
/// cancel surfaces (toast severities plus the <c>LoadFailed</c> flag pages branch on),
/// URL-driven state restoration, scroll/density persistence, the mobile card-view load path, and a
/// regression guard for the disposed-CTS race (a late grid reload after component disposal must not
/// throw ObjectDisposedException).
/// </summary>
public sealed class DataGridListPageBaseTests : BunitTestBase
{
    private readonly Mock<IToastService> _toast = new();

    public DataGridListPageBaseTests()
    {
        // Last registration wins over the Mud-backed facade the base registered, so the page's
        // error/cancel surface can be asserted without rendering a snackbar provider.
        Services.AddSingleton<IToastService>(_toast.Object);

        // The shared list-page host block: state services, an inert viewport, persistent component
        // state, and (LAST, because it freezes the provider) the interactive renderer info that
        // LoadServerDataAsync consults to bound SSR prerender fetches.
        ConfigureDataGridListPageHost();
    }

    // Public so Moq can proxy MudBlazor's Column&lt;WidgetRow&gt; over it (Castle cannot subclass a
    // generic type closed over an inaccessible argument).
    public sealed record WidgetRow(int Id, string Name);

    // A fetch that succeeded, in the Result shape the base class now takes.
    private static Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>> Loaded(int totalItems, params WidgetRow[] items)
        => Task.FromResult(Result.Success<(IReadOnlyList<WidgetRow> Items, int TotalItems)>((items, totalItems)));

    // A fetch that failed. The message is not a resource key, so the page localizer passes it
    // through verbatim and the toast text can be asserted exactly.
    private static Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>> LoadFailure(string message)
        => Task.FromResult(Result.Failure<(IReadOnlyList<WidgetRow> Items, int TotalItems)>(
            Error.Failure("Widgets.LoadFailed", message)));

    private sealed class TestGridPage : DataGridListPageBase<WidgetRow>
    {
        public Func<Dictionary<string, (string Operator, string Value)>, int, int, string?, string?, CancellationToken, Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>>> Fetch { get; set; } =
            (_, _, _, _, _, _) => Loaded(0);

        protected override string Title => "Widgets";

        public bool LoadingNow => IsLoading;

        public bool LoadFailedNow => LoadFailed;

        public int CurrentPageNow => CurrentPageState;

        public int RowsPerPageNow => RowsPerPageState;

        public bool DenseNow => DenseGrid;

        public IReadOnlyList<WidgetRow> MobileItemsNow => MobileItems;

        public int MobileTotalNow => MobileTotalItems;

        public Task<GridData<WidgetRow>> LoadAsync(
            GridState<WidgetRow> state,
            bool showCancelSnackbar = true,
            Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null) =>
            LoadServerDataAsync(state, Fetch, additionalFilters, showCancelSnackbar);

        public Task LoadMobileAsync() => LoadMobileDataAsync(Fetch);

        public void ToggleDensityNow() => ToggleDensity();

        public bool VirtualizeGridNow => VirtualizeGrid;

        public string VirtualizedGridHeightNow => VirtualizedGridHeight;

        public int VirtualizedItemSizeNow => VirtualizedItemSize;

        public Task<GridData<WidgetRow>> LoadVirtualizedAsync(
            GridStateVirtualize<WidgetRow> state,
            Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null) =>
            LoadVirtualizedServerDataAsync(state, Fetch, additionalFilters);
    }

    /// <summary>
    /// A page that holds a REAL <see cref="MudDataGrid{T}"/> as its <c>GridRef</c> and can be flipped
    /// between the paged and virtualized modes by parameter. The rows-per-page restore is only
    /// observable through a live grid, so the pager-machinery tests bind one here rather than through
    /// the grid-less <see cref="TestGridPage"/>.
    /// </summary>
    private sealed class GridBackedTestPage : DataGridListPageBase<WidgetRow>
    {
        [Parameter] public MudDataGrid<WidgetRow>? Grid { get; set; }

        [Parameter] public bool Virtualize { get; set; }

        protected override string Title => "Widgets";

        protected override MudDataGrid<WidgetRow>? GridRef => Grid;

        protected override bool VirtualizeGrid => Virtualize;
    }

    private static GridState<WidgetRow> State(int page, int pageSize, string? sortBy = null, bool descending = false)
    {
        var state = new GridState<WidgetRow> { Page = page, PageSize = pageSize };
        if (sortBy is not null)
        {
            state.SortDefinitions = [new SortDefinition<WidgetRow>(sortBy, descending, 0, row => row.Name)];
        }

        return state;
    }

    private async Task<GridData<WidgetRow>> LoadOnDispatcherAsync(
        IRenderedComponent<TestGridPage> cut,
        GridState<WidgetRow> state,
        bool showCancelSnackbar = true,
        Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null)
    {
        GridData<WidgetRow>? data = null;
        await cut.InvokeAsync(async () => data = await cut.Instance.LoadAsync(state, showCancelSnackbar, additionalFilters));
        return data!;
    }

    // == Initial load ==
    [Fact]
    public async Task LoadServerDataAsync_InitialLoad_FetchesOneBasedPageAndClearsLoading()
    {
        var cut = Render<TestGridPage>();
        var seenPageNumber = 0;
        var seenPageSize = 0;
        cut.Instance.Fetch = (_, pageNumber, pageSize, _, _, _) =>
        {
            seenPageNumber = pageNumber;
            seenPageSize = pageSize;
            return Loaded(40, new WidgetRow(1, "First"), new WidgetRow(2, "Second"));
        };

        var data = await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        data.TotalItems.Should().Be(40);
        data.Items.Should().HaveCount(2);
        seenPageNumber.Should().Be(1, "the grid's 0-indexed page becomes a 1-indexed API page");
        seenPageSize.Should().Be(10);
        cut.Instance.LoadingNow.Should().BeFalse();
        cut.Instance.LoadFailedNow.Should().BeFalse("a successful fetch never raises the failure flag");
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoadServerDataAsync_AppliesAdditionalFiltersBeforeFetch()
    {
        var cut = Render<TestGridPage>();
        Dictionary<string, (string Operator, string Value)>? seenFilters = null;
        cut.Instance.Fetch = (filters, _, _, _, _, _) =>
        {
            seenFilters = filters;
            return Loaded(0);
        };

        await LoadOnDispatcherAsync(
            cut,
            State(page: 0, pageSize: 10),
            additionalFilters: filters => filters["search"] = ("contains", "blue"));

        seenFilters.Should().NotBeNull();
        seenFilters!.Should().ContainKey("search").WhoseValue.Should().Be(("contains", "blue"));
    }

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

    // == Grid filter extraction ==
    [Fact]
    public async Task LoadServerDataAsync_TranslatesGridFiltersIntoTheFetchDictionary()
    {
        var cut = Render<TestGridPage>();
        Dictionary<string, (string Operator, string Value)>? seenFilters = null;
        cut.Instance.Fetch = (filters, _, _, _, _, _) =>
        {
            seenFilters = filters;
            return Loaded(0);
        };

        var state = State(page: 0, pageSize: 10);
        state.FilterDefinitions = [Filter("Name", "contains", "blue")];

        await LoadOnDispatcherAsync(cut, state);

        seenFilters.Should().NotBeNull();
        seenFilters!.Should().ContainKey("Name").WhoseValue.Should().Be(("contains", "blue"));
    }

    [Fact]
    public async Task LoadServerDataAsync_WithTwoFiltersOnTheSameColumn_KeepsTheNewestAndStillLoads()
    {
        // MudDataGrid lets the user add a second filter row on a column that is already filtered.
        // The fetch contract carries one filter per column, so the projection used to throw
        // ArgumentException ("an item with the same key has already been added") BEFORE the try
        // block, stranding IsLoading at true and leaving the grid spinning forever.
        var cut = Render<TestGridPage>();
        Dictionary<string, (string Operator, string Value)>? seenFilters = null;
        cut.Instance.Fetch = (filters, _, _, _, _, _) =>
        {
            seenFilters = filters;
            return Loaded(0);
        };

        var state = State(page: 0, pageSize: 10);
        state.FilterDefinitions =
        [
            Filter("Name", "contains", "blue"),
            Filter("Name", "equals", "green")
        ];

        var act = async () => await LoadOnDispatcherAsync(cut, state);

        await act.Should().NotThrowAsync();
        seenFilters.Should().NotBeNull();
        seenFilters!.Should().ContainKey("Name").WhoseValue.Should().Be(("equals", "green"),
            "the newest filter row wins when a column carries more than one");
        cut.Instance.LoadingNow.Should().BeFalse("the loading flag must always be reset");
    }

    [Fact]
    public async Task LoadServerDataAsync_WhenAdditionalFiltersCallbackThrows_ReportsAndResetsLoading()
    {
        // The callback is arbitrary page code; a throw from it used to escape past the finally.
        var cut = Render<TestGridPage>();

        var data = await LoadOnDispatcherAsync(
            cut,
            State(page: 0, pageSize: 10),
            additionalFilters: _ => throw new InvalidOperationException("bad filter"));

        data.Items.Should().BeEmpty();
        cut.Instance.LoadingNow.Should().BeFalse();
        _toast.Verify(t => t.Error(It.IsAny<string>()), Times.Once);
    }

    // == State persistence: in-memory service + URL mirror ==
    [Fact]
    public async Task LoadServerDataAsync_PersistsPageSortState_AndMirrorsItToUrl()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => Loaded(100, new WidgetRow(1, "First"));

        await LoadOnDispatcherAsync(cut, State(page: 2, pageSize: 25, sortBy: "Name", descending: true));

        var saved = Services.GetRequiredService<ListPageStateService>().GetState("/");
        saved.Should().NotBeNull();
        saved!.Page.Should().Be(2);
        saved.PageSize.Should().Be(25);
        saved.SortColumn.Should().Be("Name");
        saved.SortDescending.Should().BeTrue();
        navigation.Uri.Should().Contain("p=2").And.Contain("ps=25").And.Contain("s=Name").And.Contain("sd=desc");
    }

    // == Error and cancel surfaces ==
    [Fact]
    public async Task LoadServerDataAsync_WhenFetchFails_ReturnsEmptyGridSetsLoadFailedAndRaisesOneErrorToast()
    {
        // A failed Result is the surface a thrown exception used to be: one toast carrying the
        // localized message, LoadFailed raised so the page can offer a retry instead of the "no
        // records" empty state, and zero rows in the grid.
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => LoadFailure("The widget service is unavailable.");

        var data = await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        data.Items.Should().BeEmpty();
        data.TotalItems.Should().Be(0);
        cut.Instance.LoadFailedNow.Should().BeTrue();
        cut.Instance.LoadingNow.Should().BeFalse("the loading flag must always be reset");
        _toast.Verify(t => t.Show("The widget service is unavailable.", ToastSeverity.Error), Times.Once);
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoadServerDataAsync_WhenAFailureCarriesSeveralErrors_StillRaisesExactlyOneToast()
    {
        // Result.Combine aggregates; the page must read as one sentence, not one toast per error.
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => Task.FromResult(
            Result.Failure<(IReadOnlyList<WidgetRow> Items, int TotalItems)>(
            [
                Error.Validation("Widgets.BadPage", "Page is out of range."),
                Error.Unexpected("Widgets.Backend", "The widget service is unavailable."),
            ]));

        await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        // Most severe first: the backend failure leads and the validation note follows.
        _toast.Verify(
            t => t.Show("The widget service is unavailable. Page is out of range.", ToastSeverity.Error),
            Times.Once);
    }

    [Fact]
    public async Task LoadServerDataAsync_AfterAFailedLoad_ASuccessfulReloadClearsLoadFailed()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => LoadFailure("The widget service is unavailable.");
        await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));
        cut.Instance.LoadFailedNow.Should().BeTrue();

        cut.Instance.Fetch = (_, _, _, _, _, _) => Loaded(1, new WidgetRow(1, "First"));
        var data = await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        data.Items.Should().HaveCount(1);
        cut.Instance.LoadFailedNow.Should().BeFalse("the next fetch resets the flag");
    }

    [Fact]
    public async Task LoadServerDataAsync_WhenFetchThrows_ReturnsEmptyGridAndRaisesErrorToast()
    {
        // The catch stayed for page-supplied callbacks, so a throw still lands on the same surface.
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => throw new InvalidOperationException("backend down");

        var data = await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        data.Items.Should().BeEmpty();
        data.TotalItems.Should().Be(0);
        cut.Instance.LoadingNow.Should().BeFalse();
        cut.Instance.LoadFailedNow.Should().BeTrue();
        _toast.Verify(t => t.Error(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task LoadServerDataAsync_WhenFetchCanceled_ReturnsEmptyGridAndRaisesInfoToast()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => throw new OperationCanceledException();

        var data = await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        data.Items.Should().BeEmpty();
        _toast.Verify(t => t.Info(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task LoadServerDataAsync_WhenCanceledWithToastSuppressed_StaysQuiet()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => throw new OperationCanceledException();

        await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10), showCancelSnackbar: false);

        _toast.VerifyNoOtherCalls();
    }

    // == Disposed-CTS regression ==
    [Fact]
    public async Task LoadServerDataAsync_AfterComponentDisposal_ToleratesDisposedCtsAndStillLoads()
    {
        // Regression guard: a debounced grid reload (e.g. a search-box blur) can fire AFTER the page
        // disposed its CancellationTokenSource; cancelling the disposed source used to surface as an
        // unhandled ObjectDisposedException that tripped the blazor-error-ui banner.
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => Loaded(1, new WidgetRow(1, "First"));
        await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        GridData<WidgetRow>? late = null;
        var act = async () => await cut.InvokeAsync(async () => late = await cut.Instance.LoadAsync(State(page: 0, pageSize: 10)));

        await act.Should().NotThrowAsync();
        late.Should().NotBeNull();
        late!.TotalItems.Should().Be(1);
    }

    // == URL-driven state restoration ==
    [Fact]
    public void OnInitialized_WithStateInUrl_RestoresPagePageSizeAndDensity()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/widgets?p=3&ps=50&s=Name&sd=desc&d=1");

        var cut = Render<TestGridPage>();

        cut.Instance.CurrentPageNow.Should().Be(3);
        cut.Instance.RowsPerPageNow.Should().Be(50);
        cut.Instance.DenseNow.Should().BeTrue();
    }

    // == Scroll and density persistence ==
    [Fact]
    public void OnScrollPositionChanged_UpdatesScrollPositionInStateService()
    {
        var cut = Render<TestGridPage>();

        cut.Instance.OnScrollPositionChanged(123.5);

        var saved = Services.GetRequiredService<ListPageStateService>().GetState("/");
        saved.Should().NotBeNull();
        saved!.ScrollPosition.Should().Be(123.5);
    }

    [Fact]
    public async Task ToggleDensity_FlipsDensityAndPersistsIt()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<TestGridPage>();

        await cut.InvokeAsync(cut.Instance.ToggleDensityNow);

        cut.Instance.DenseNow.Should().BeTrue();
        Services.GetRequiredService<ListPageStateService>().GetState("/")!.DenseGrid.Should().BeTrue();
        navigation.Uri.Should().Contain("d=1");
    }

    // == Mobile card-view load path ==
    [Fact]
    public async Task LoadMobileDataAsync_PopulatesMobileItemsAndTotal()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => Loaded(8, new WidgetRow(1, "First"), new WidgetRow(2, "Second"));

        await cut.InvokeAsync(() => cut.Instance.LoadMobileAsync());

        cut.Instance.MobileItemsNow.Should().HaveCount(2);
        cut.Instance.MobileTotalNow.Should().Be(8);
        cut.Instance.LoadingNow.Should().BeFalse();
        cut.Instance.LoadFailedNow.Should().BeFalse();
    }

    [Fact]
    public async Task LoadMobileDataAsync_WhenFetchFails_EmptiesTheCardsAndRaisesOneErrorToast()
    {
        // The card view has no NoRecordsContent to branch in, so LoadFailed is the only way the page
        // can tell "nothing matched" apart from "the fetch failed".
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => Loaded(8, new WidgetRow(1, "First"));
        await cut.InvokeAsync(() => cut.Instance.LoadMobileAsync());

        cut.Instance.Fetch = (_, _, _, _, _, _) => LoadFailure("The widget service is unavailable.");
        await cut.InvokeAsync(() => cut.Instance.LoadMobileAsync());

        cut.Instance.MobileItemsNow.Should().BeEmpty();
        cut.Instance.MobileTotalNow.Should().Be(0);
        cut.Instance.LoadFailedNow.Should().BeTrue();
        cut.Instance.LoadingNow.Should().BeFalse();
        _toast.Verify(t => t.Show("The widget service is unavailable.", ToastSeverity.Error), Times.Once);
    }

    [Fact]
    public async Task LoadMobileDataAsync_DoesNotOverwriteThePersistedRowsPerPage()
    {
        // The mobile card fetch shares the persisted state with the desktop grid, and MobilePageSize
        // is never restored from it. Writing it there only clobbered the user's real RowsPerPage:
        // set 50 rows, narrow the viewport past the mobile breakpoint, come back to 10.
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/widgets?p=0&ps=50");
        var cut = Render<TestGridPage>();
        cut.Instance.RowsPerPageNow.Should().Be(50);
        cut.Instance.Fetch = (_, _, _, _, _, _) => Loaded(8, new WidgetRow(1, "First"));

        await cut.InvokeAsync(() => cut.Instance.LoadMobileAsync());

        var saved = Services.GetRequiredService<ListPageStateService>().GetState("/widgets");
        saved.Should().NotBeNull();
        saved!.PageSize.Should().Be(
            0,
            "0 is skipped by the restore guards, exactly as the virtualized path already saves it");
        navigation.Uri.Should().NotContain(
            "ps=10",
            "MobilePageSize is a layout constant, not a row-count preference the user expressed");
    }

    // == Virtualization: window arithmetic ==
    [Theory]
    // startIndex, count -> firstPage, pageSize, offset, needsSecondPage
    [InlineData(0, 10, 1, 10, 0, false)] // the very first window
    [InlineData(20, 10, 3, 10, 0, false)] // aligned: exactly one page
    [InlineData(25, 10, 3, 10, 5, true)] // unaligned: spills into the next page
    [InlineData(7, 5, 2, 5, 2, true)] // unaligned again, different page size
    [InlineData(0, 0, 1, 1, 0, false)] // count 0: page size clamps to 1, nothing spills
    [InlineData(0, -4, 1, 1, 0, false)] // a negative count is clamped the same way
    public void ComputeVirtualWindow_MapsTheRowWindowOntoPagedFetches(
        int startIndex, int count, int firstPage, int pageSize, int offset, bool needsSecondPage)
    {
        var window = DataGridListPageBase<WidgetRow>.ComputeVirtualWindow(startIndex, count);

        window.FirstPage.Should().Be(firstPage, "pages are 1-based for the fetch delegate");
        window.PageSize.Should().Be(pageSize);
        window.Offset.Should().Be(offset);
        window.NeedsSecondPage.Should().Be(needsSecondPage);
    }

    private static GridStateVirtualize<WidgetRow> VirtualState(
        int startIndex, int count, string? sortBy = null, bool descending = false)
    {
        var state = new GridStateVirtualize<WidgetRow> { StartIndex = startIndex, Count = count };
        if (sortBy is not null)
        {
            state.SortDefinitions = [new SortDefinition<WidgetRow>(sortBy, descending, 0, row => row.Name)];
        }

        return state;
    }

    private async Task<GridData<WidgetRow>> LoadVirtualizedOnDispatcherAsync(
        IRenderedComponent<TestGridPage> cut,
        GridStateVirtualize<WidgetRow> state,
        Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null)
    {
        GridData<WidgetRow>? data = null;
        await cut.InvokeAsync(async () => data = await cut.Instance.LoadVirtualizedAsync(state, additionalFilters));
        return data!;
    }

    // == Virtualization: the VirtualizeServerData funnel ==
    [Fact]
    public async Task LoadVirtualizedServerDataAsync_AlignedWindow_FetchesOnePageAndReturnsIt()
    {
        var cut = Render<TestGridPage>();
        var calls = new List<(int PageNumber, int PageSize)>();
        cut.Instance.Fetch = (_, pageNumber, pageSize, _, _, _) =>
        {
            calls.Add((pageNumber, pageSize));
            return Loaded(40, new WidgetRow(1, "First"), new WidgetRow(2, "Second"), new WidgetRow(3, "Third"));
        };

        var data = await LoadVirtualizedOnDispatcherAsync(cut, VirtualState(startIndex: 3, count: 3));

        calls.Should().ContainSingle("an aligned window is exactly one page")
            .Which.Should().Be((2, 3));
        data.Items.Should().HaveCount(3);
        data.TotalItems.Should().Be(40, "the fetch result carries the unpaginated total");
        cut.Instance.LoadingNow.Should().BeFalse();
        cut.Instance.LoadFailedNow.Should().BeFalse();
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoadVirtualizedServerDataAsync_UnalignedWindow_FetchesBothPagesAndSlicesToCount()
    {
        // startIndex 4 / count 3 -> page size 3, first page 2 (rows 3-5), offset 1, so the window
        // (rows 4-6) needs page 3 as well and the concatenation is sliced from the second row.
        var cut = Render<TestGridPage>();
        var calls = new List<int>();
        cut.Instance.Fetch = (_, pageNumber, _, _, _, _) =>
        {
            calls.Add(pageNumber);
            return pageNumber == 2
                ? Loaded(40, new WidgetRow(4, "D"), new WidgetRow(5, "E"), new WidgetRow(6, "F"))
                : Loaded(40, new WidgetRow(7, "G"), new WidgetRow(8, "H"), new WidgetRow(9, "I"));
        };

        var data = await LoadVirtualizedOnDispatcherAsync(cut, VirtualState(startIndex: 4, count: 3));

        calls.Should().Equal(2, 3);
        // The concatenated pages are sliced to exactly the requested window.
        data.Items.Select(r => r.Name).Should().Equal("E", "F", "G");
        data.TotalItems.Should().Be(40);
    }

    [Fact]
    public async Task LoadVirtualizedServerDataAsync_AtTheEndOfTheData_ReturnsFewerRowsThanRequested()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, pageNumber, _, _, _, _) => pageNumber == 2
            ? Loaded(7, new WidgetRow(4, "D"), new WidgetRow(5, "E"), new WidgetRow(6, "F"))
            : Loaded(7, new WidgetRow(7, "G"));

        var data = await LoadVirtualizedOnDispatcherAsync(cut, VirtualState(startIndex: 4, count: 3));

        data.Items.Select(r => r.Name).Should().Equal("E", "F", "G");
        data.TotalItems.Should().Be(7);
    }

    [Fact]
    public async Task LoadVirtualizedServerDataAsync_WhenFetchFails_SetsLoadFailedAndReturnsAnEmptyGrid()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => LoadFailure("The widget service is unavailable.");

        var act = async () => await LoadVirtualizedOnDispatcherAsync(cut, VirtualState(startIndex: 0, count: 10));
        await act.Should().NotThrowAsync();

        var data = await LoadVirtualizedOnDispatcherAsync(cut, VirtualState(startIndex: 0, count: 10));
        data.Items.Should().BeEmpty();
        data.TotalItems.Should().Be(0);
        cut.Instance.LoadFailedNow.Should().BeTrue();
        cut.Instance.LoadingNow.Should().BeFalse("the loading flag must always be reset");
    }

    [Fact]
    public async Task LoadVirtualizedServerDataAsync_AppliesAdditionalFilters()
    {
        var cut = Render<TestGridPage>();
        Dictionary<string, (string Operator, string Value)>? seenFilters = null;
        cut.Instance.Fetch = (filters, _, _, _, _, _) =>
        {
            seenFilters = filters;
            return Loaded(0);
        };

        await LoadVirtualizedOnDispatcherAsync(
            cut,
            VirtualState(startIndex: 0, count: 10),
            additionalFilters: filters => filters["search"] = ("contains", "blue"));

        seenFilters.Should().NotBeNull();
        seenFilters!.Should().ContainKey("search").WhoseValue.Should().Be(("contains", "blue"));
    }

    [Fact]
    public async Task LoadVirtualizedServerDataAsync_MapsSortDefinitionsIntoTheFetchCall()
    {
        var cut = Render<TestGridPage>();
        string? seenColumn = null;
        string? seenDirection = null;
        cut.Instance.Fetch = (_, _, _, sortColumn, sortDirection, _) =>
        {
            seenColumn = sortColumn;
            seenDirection = sortDirection;
            return Loaded(0);
        };

        await LoadVirtualizedOnDispatcherAsync(
            cut,
            VirtualState(startIndex: 0, count: 10, sortBy: "Name", descending: true));

        seenColumn.Should().Be("Name", "GridStateVirtualize carries the same SortDefinitions as GridState");
        seenDirection.Should().Be("desc");
    }

    [Fact]
    public async Task LoadVirtualizedServerDataAsync_WhenFetchCanceled_StaysSilent()
    {
        // Scroll bursts supersede in-flight window fetches constantly, so a cancel toast would fire
        // continuously during ordinary scrolling. The paged funnel still toasts; this one never does.
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => throw new OperationCanceledException();

        var data = await LoadVirtualizedOnDispatcherAsync(cut, VirtualState(startIndex: 0, count: 10));

        data.Items.Should().BeEmpty();
        cut.Instance.LoadFailedNow.Should().BeFalse("a superseded window is not a failure");
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoadVirtualizedServerDataAsync_PersistsSortWithoutMirroringPagerStateToTheUrl()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => Loaded(100, new WidgetRow(1, "First"));

        await LoadVirtualizedOnDispatcherAsync(
            cut,
            VirtualState(startIndex: 40, count: 20, sortBy: "Name", descending: true));

        var saved = Services.GetRequiredService<ListPageStateService>().GetState("/");
        saved.Should().NotBeNull();
        saved!.SortColumn.Should().Be("Name");
        saved.SortDescending.Should().BeTrue();
        saved.Page.Should().Be(0, "a virtualized grid has no page number to remember");
        saved.PageSize.Should().Be(0);
        navigation.Uri.Should().Contain("s=Name").And.Contain("sd=desc");
        navigation.Uri.Should().NotContain("p=").And.NotContain("ps=");
    }

    // == Virtualization: opt-in defaults and the skipped pager machinery ==
    [Fact]
    public void VirtualizeGrid_DefaultsToFalse_WithTheDocumentedHeightAndItemSize()
    {
        var cut = Render<TestGridPage>();

        cut.Instance.VirtualizeGridNow.Should().BeFalse("virtualization is opt-in, never the default");
        cut.Instance.VirtualizedGridHeightNow.Should().Be("70vh");
        cut.Instance.VirtualizedItemSizeNow.Should().Be(52);
    }

    [Fact]
    public void RestoreGridState_WhenPaged_RestoresRowsPerPageOntoTheGrid()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/widgets?p=0&ps=50");
        var grid = Render<MudDataGrid<WidgetRow>>().Instance;

        Render<GridBackedTestPage>(ps => ps
            .Add(p => p.Grid, grid)
            .Add(p => p.Virtualize, false));

        grid.RowsPerPage.Should().Be(50, "the paged page restores the saved rows-per-page after first render");
    }

    [Fact]
    public void RestoreGridState_WhenVirtualized_SkipsTheRowsPerPageRestore()
    {
        // Same saved state, same grid: the only difference is the opt-in flag, and it must short-circuit
        // the whole pager-restore block (a virtualized grid renders no pager to restore).
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/widgets?p=0&ps=50");
        var grid = Render<MudDataGrid<WidgetRow>>().Instance;

        Render<GridBackedTestPage>(ps => ps
            .Add(p => p.Grid, grid)
            .Add(p => p.Virtualize, true));

        grid.RowsPerPage.Should().Be(10, "MudDataGrid's own default is left untouched");
    }
}
