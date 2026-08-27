using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// bUnit host tests for <see cref="DataGridListPageBase{TDto}"/> through a minimal concrete page:
/// initial server-data load, page/sort state persistence mirrored to the URL, the failed-fetch and
/// cancel surfaces (snackbar severities plus the <c>LoadFailed</c> flag pages branch on),
/// URL-driven state restoration, scroll/density persistence, the mobile card-view load path, and a
/// regression guard for the disposed-CTS race (a late grid reload after component disposal must not
/// throw ObjectDisposedException).
/// </summary>
public sealed class DataGridListPageBaseTests : BunitTestBase
{
    private readonly Mock<ISnackbar> _snackbar = new();

    public DataGridListPageBaseTests()
    {
        Services.AddScoped<ListPageStateService>();
        Services.AddScoped<ListPageQueryStateService>();
        // Last registration wins over the SnackbarService that AddMudServices registered, so the
        // page's error/cancel surface can be asserted without rendering a snackbar provider.
        Services.AddSingleton<ISnackbar>(_snackbar.Object);
        AddBunitPersistentComponentState();
        // LoadServerDataAsync consults RendererInfo.IsInteractive to bound SSR prerender fetches.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    // Public so Moq can proxy MudBlazor's Column&lt;WidgetRow&gt; over it (Castle cannot subclass a
    // generic type closed over an inaccessible argument).
    public sealed record WidgetRow(int Id, string Name);

    // A fetch that succeeded, in the Result shape the base class now takes.
    private static Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>> Loaded(int totalItems, params WidgetRow[] items)
        => Task.FromResult(Result.Success<(IReadOnlyList<WidgetRow> Items, int TotalItems)>((items, totalItems)));

    // A fetch that failed. The message is not a resource key, so the page localizer passes it
    // through verbatim and the snackbar text can be asserted exactly.
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
        _snackbar.Verify(
            s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
            Times.Never);
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
        _snackbar.Verify(
            s => s.Add(It.IsAny<string>(), Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
            Times.Once);
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
    public async Task LoadServerDataAsync_WhenFetchFails_ReturnsEmptyGridSetsLoadFailedAndRaisesOneErrorSnackbar()
    {
        // A failed Result is the surface a thrown exception used to be: one snackbar carrying the
        // localized message, LoadFailed raised so the page can offer a retry instead of the "no
        // records" empty state, and zero rows in the grid.
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => LoadFailure("The widget service is unavailable.");

        var data = await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        data.Items.Should().BeEmpty();
        data.TotalItems.Should().Be(0);
        cut.Instance.LoadFailedNow.Should().BeTrue();
        cut.Instance.LoadingNow.Should().BeFalse("the loading flag must always be reset");
        _snackbar.Verify(
            s => s.Add(
                "The widget service is unavailable.",
                Severity.Error,
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()),
            Times.Once);
        _snackbar.Verify(
            s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task LoadServerDataAsync_WhenAFailureCarriesSeveralErrors_StillRaisesExactlyOneSnackbar()
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
        _snackbar.Verify(
            s => s.Add(
                "The widget service is unavailable. Page is out of range.",
                Severity.Error,
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()),
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
    public async Task LoadServerDataAsync_WhenFetchThrows_ReturnsEmptyGridAndRaisesErrorSnackbar()
    {
        // The catch stayed for page-supplied callbacks, so a throw still lands on the same surface.
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => throw new InvalidOperationException("backend down");

        var data = await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        data.Items.Should().BeEmpty();
        data.TotalItems.Should().Be(0);
        cut.Instance.LoadingNow.Should().BeFalse();
        cut.Instance.LoadFailedNow.Should().BeTrue();
        _snackbar.Verify(
            s => s.Add(It.IsAny<string>(), Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task LoadServerDataAsync_WhenFetchCanceled_ReturnsEmptyGridAndRaisesInfoSnackbar()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => throw new OperationCanceledException();

        var data = await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        data.Items.Should().BeEmpty();
        _snackbar.Verify(
            s => s.Add(It.IsAny<string>(), Severity.Info, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task LoadServerDataAsync_WhenCanceledWithSnackbarSuppressed_StaysQuiet()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => throw new OperationCanceledException();

        await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10), showCancelSnackbar: false);

        _snackbar.Verify(
            s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
            Times.Never);
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
    public async Task LoadMobileDataAsync_WhenFetchFails_EmptiesTheCardsAndRaisesOneErrorSnackbar()
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
        _snackbar.Verify(
            s => s.Add(
                "The widget service is unavailable.",
                Severity.Error,
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()),
            Times.Once);
    }
}
