using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// bUnit host tests for <see cref="DataGridListPageBase{TDto}"/> through a minimal concrete page:
/// initial server-data load, page/sort state persistence mirrored to the URL, error and cancel
/// surfaces (snackbar severities), URL-driven state restoration, scroll/density persistence, the
/// mobile card-view load path, and a regression guard for the disposed-CTS race (a late grid reload
/// after component disposal must not throw ObjectDisposedException).
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

    private sealed record WidgetRow(int Id, string Name);

    private sealed class TestGridPage : DataGridListPageBase<WidgetRow>
    {
        public Func<Dictionary<string, (string Operator, string Value)>, int, int, string?, string?, CancellationToken, Task<(IReadOnlyList<WidgetRow> Items, int TotalItems)>> Fetch { get; set; } =
            (_, _, _, _, _, _) => Task.FromResult<(IReadOnlyList<WidgetRow> Items, int TotalItems)>(([], 0));

        protected override string Title => "Widgets";

        public bool LoadingNow => IsLoading;

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
            return Task.FromResult<(IReadOnlyList<WidgetRow> Items, int TotalItems)>(
                ([new WidgetRow(1, "First"), new WidgetRow(2, "Second")], 40));
        };

        var data = await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        data.TotalItems.Should().Be(40);
        data.Items.Should().HaveCount(2);
        seenPageNumber.Should().Be(1, "the grid's 0-indexed page becomes a 1-indexed API page");
        seenPageSize.Should().Be(10);
        cut.Instance.LoadingNow.Should().BeFalse();
    }

    [Fact]
    public async Task LoadServerDataAsync_AppliesAdditionalFiltersBeforeFetch()
    {
        var cut = Render<TestGridPage>();
        Dictionary<string, (string Operator, string Value)>? seenFilters = null;
        cut.Instance.Fetch = (filters, _, _, _, _, _) =>
        {
            seenFilters = filters;
            return Task.FromResult<(IReadOnlyList<WidgetRow> Items, int TotalItems)>(([], 0));
        };

        await LoadOnDispatcherAsync(
            cut,
            State(page: 0, pageSize: 10),
            additionalFilters: filters => filters["search"] = ("contains", "blue"));

        seenFilters.Should().NotBeNull();
        seenFilters!.Should().ContainKey("search").WhoseValue.Should().Be(("contains", "blue"));
    }

    // == State persistence: in-memory service + URL mirror ==
    [Fact]
    public async Task LoadServerDataAsync_PersistsPageSortState_AndMirrorsItToUrl()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) =>
            Task.FromResult<(IReadOnlyList<WidgetRow> Items, int TotalItems)>(([new WidgetRow(1, "First")], 100));

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
    public async Task LoadServerDataAsync_WhenFetchThrows_ReturnsEmptyGridAndRaisesErrorSnackbar()
    {
        var cut = Render<TestGridPage>();
        cut.Instance.Fetch = (_, _, _, _, _, _) => throw new InvalidOperationException("backend down");

        var data = await LoadOnDispatcherAsync(cut, State(page: 0, pageSize: 10));

        data.Items.Should().BeEmpty();
        data.TotalItems.Should().Be(0);
        cut.Instance.LoadingNow.Should().BeFalse();
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
        cut.Instance.Fetch = (_, _, _, _, _, _) =>
            Task.FromResult<(IReadOnlyList<WidgetRow> Items, int TotalItems)>(([new WidgetRow(1, "First")], 1));
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
        cut.Instance.Fetch = (_, _, _, _, _, _) =>
            Task.FromResult<(IReadOnlyList<WidgetRow> Items, int TotalItems)>(
                ([new WidgetRow(1, "First"), new WidgetRow(2, "Second")], 8));

        await cut.InvokeAsync(() => cut.Instance.LoadMobileAsync());

        cut.Instance.MobileItemsNow.Should().HaveCount(2);
        cut.Instance.MobileTotalNow.Should().Be(8);
        cut.Instance.LoadingNow.Should().BeFalse();
    }
}
