using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Lists;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services;
using Moq;
using MudBlazor;
using MudBlazor.Services;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// <c>SaveFilterState</c> and <c>ReloadActiveLayoutAsync</c> are how a page whose phone list
/// fetches through its own delegate persists a filter change: that fetch path saves nothing, so a
/// stale value would otherwise be restored when the user comes back (ADC Run 16, D6).
/// </summary>
public sealed class DataGridListPageBaseFilterStateTests : BunitTestBase
{
    private const string Route = "/things";

    private static readonly BrowserWindowSize PhoneViewport = new() { Width = 390, Height = 844 };

    public DataGridListPageBaseFilterStateTests()
    {
        Services.AddSingleton(new Mock<IToastService>().Object);
        ConfigureDataGridListPageHost();
    }

    public sealed record Row(int Id);

    private sealed class FilteredPage : DataGridListPageBase<Row>
    {
        public string Mode { get; set; } = "all";

        protected override string Title => "Rows";

        public void Save() => SaveFilterState();

        public Task Reload(MobileInfiniteScrollList<Row>? list) => ReloadActiveLayoutAsync(list);

        public int MobileDataRequests { get; private set; }

        protected override void SaveFilters(Dictionary<string, string> filters) => filters["mode"] = Mode;

        protected override void RestoreFilters(IReadOnlyDictionary<string, string> filters) =>
            Mode = filters.TryGetValue("mode", out var mode) ? mode : "all";

        protected override Task OnMobileDataRequestedAsync()
        {
            MobileDataRequests++;
            return Task.CompletedTask;
        }
    }

    // A phone page that exposes its card list, as GridRef exposes the grid.
    private sealed class CardListPage : DataGridListPageBase<Row>
    {
        [Parameter] public MobileInfiniteScrollList<Row>? List { get; set; }

        public string Mode { get; private set; } = "all";

        protected override string Title => "Rows";

        protected override MobileInfiniteScrollList<Row>? MobileListRef => List;

        protected override void SaveFilters(Dictionary<string, string> filters) => filters["mode"] = Mode;

        protected override void RestoreFilters(IReadOnlyDictionary<string, string> filters) =>
            Mode = filters.TryGetValue("mode", out var mode) ? mode : "all";
    }

    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    private ListPageStateService State => Services.GetRequiredService<ListPageStateService>();

    [Fact]
    public async Task SaveFilterState_WritesTheCurrentFiltersToTheStateAndTheUrl()
    {
        Navigation.NavigateTo(Route);
        var cut = Render<FilteredPage>();

        cut.Instance.Mode = "mine";
        await cut.InvokeAsync(cut.Instance.Save);

        State.GetState(Route)!.Filters["mode"].Should().Be("mine");
        Uri.UnescapeDataString(new Uri(Navigation.Uri).Query).Should().Contain("f:mode=mine");
    }

    [Fact]
    public async Task SaveFilterState_KeepsThePagerAndSortAlreadySaved()
    {
        Navigation.NavigateTo(Route);
        State.SaveState(Route, new ListPageState { Page = 3, PageSize = 50, SortColumn = "Name", SortDescending = true });
        var cut = Render<FilteredPage>();

        await cut.InvokeAsync(cut.Instance.Save);

        var saved = State.GetState(Route)!;
        saved.Page.Should().Be(3);
        saved.PageSize.Should().Be(50);
        saved.SortColumn.Should().Be("Name");
        saved.SortDescending.Should().BeTrue();
    }

    [Fact]
    public async Task SaveFilterState_AfterTheUserLeftThePage_WritesNothing()
    {
        Navigation.NavigateTo(Route);
        var cut = Render<FilteredPage>();
        Navigation.NavigateTo("/elsewhere");

        cut.Instance.Mode = "mine";
        await cut.InvokeAsync(cut.Instance.Save);

        State.GetState(Route)?.Filters.Should().NotContainKey("mode");
        new Uri(Navigation.Uri).AbsolutePath.Should().Be("/elsewhere");
    }

    // The card list fetches through the page's own delegate, which saves nothing: the reload a
    // filter change goes through is the one place that can save it on a phone.
    [Fact]
    public async Task ReloadActiveLayout_OnAPhone_SavesTheFiltersAndResetsTheCardList()
    {
        Navigation.NavigateTo(Route);
        var cut = Render<FilteredPage>();
        await GoToPhoneAsync(cut);
        var fetches = 0;
        var list = RenderCardList(() => fetches++);
        var before = fetches;

        cut.Instance.Mode = "mine";
        await cut.InvokeAsync(() => cut.Instance.Reload(list.Instance));

        State.GetState(Route)!.Filters["mode"].Should().Be("mine");
        Uri.UnescapeDataString(new Uri(Navigation.Uri).Query).Should().Contain("f:mode=mine");
        fetches.Should().BeGreaterThan(before);
    }

    [Fact]
    public async Task ReloadActiveLayout_OnADesktop_LeavesTheCardListAndTheStateAlone()
    {
        Navigation.NavigateTo(Route);
        var cut = Render<FilteredPage>();
        var fetches = 0;
        var list = RenderCardList(() => fetches++);
        var before = fetches;

        cut.Instance.Mode = "mine";
        await cut.InvokeAsync(() => cut.Instance.Reload(list.Instance));

        fetches.Should().Be(before);
        State.GetState(Route)?.Filters.Should().NotContainKey("mode");
    }

    [Fact]
    public async Task ReloadActiveLayout_WithNeitherLayoutRendered_IsANoOp()
    {
        Navigation.NavigateTo(Route);
        var cut = Render<FilteredPage>();
        await GoToPhoneAsync(cut);

        var act = async () => await cut.InvokeAsync(() => cut.Instance.Reload(null));

        await act.Should().NotThrowAsync();
    }

    // Grid column filters (Store Run 4 AI-31): saved to the URL and state, restored onto the grid.
    [Fact]
    public async Task ColumnFilter_TheUserSets_IsWrittenToTheUrlAndTheSavedState()
    {
        Navigation.NavigateTo(Route);
        var (grid, _, seen) = RenderGridPage();

        grid.Instance.FilterDefinitions.Add(new FilterDefinition<GridRow> { Column = Column(grid, "Name"), Operator = "contains", Value = "Zed" });
        await grid.InvokeAsync(grid.Instance.ReloadServerData);

        seen[^1].Should().ContainKey("Name").WhoseValue.Should().Be(("contains", "Zed"));
        State.GetState(Route)!.ColumnFilters["Name"].Should().Be(new ColumnFilterState("contains", "Zed"));
        Uri.UnescapeDataString(new Uri(Navigation.Uri).Query).Should().Contain("c:Name=contains:Zed");
    }

    [Fact]
    public async Task ColumnFilterInTheUrl_IsSentByTheFirstLoadAndPutBackOnTheGrid()
    {
        Navigation.NavigateTo(Route + "?c:Name=contains%3AAnn&c:Id=%3E%3D%3A5");
        var (grid, _, seen) = RenderGridPage();

        await grid.InvokeAsync(grid.Instance.ReloadServerData);

        seen[^1].Should().ContainKey("Name").WhoseValue.Should().Be(("contains", "Ann"));
        grid.Instance.FilterDefinitions.Should().HaveCount(2);
        var id = grid.Instance.FilterDefinitions.Single(f => f.Column!.PropertyName == "Id");
        id.Operator.Should().Be(">=");
        id.Value.Should().Be(5, "the value is parsed back to the column's type");
        Uri.UnescapeDataString(new Uri(Navigation.Uri).Query).Should().Contain("c:Name=contains:Ann", "the restored filter stays in the URL");
    }

    [Fact]
    public async Task ColumnFilterOnAColumnTheGridDoesNotRender_IsDropped()
    {
        Navigation.NavigateTo(Route + "?c:Bogus=contains%3Ax&c:Name=equals%3AAnn");
        var (grid, _, seen) = RenderGridPage();

        await grid.InvokeAsync(grid.Instance.ReloadServerData);

        seen[^1].Should().NotContainKey("Bogus");
        grid.Instance.FilterDefinitions.Should().ContainSingle().Which.Column!.PropertyName.Should().Be("Name");
        State.GetState(Route)!.ColumnFilters.Should().NotContainKey("Bogus");
    }

    [Fact]
    public async Task BackToAnUnfilteredUrl_ClearsTheGridsColumnFilters()
    {
        Navigation.NavigateTo(Route + "?c:Name=contains%3AAnn");
        var (grid, _, seen) = RenderGridPage();
        await grid.InvokeAsync(grid.Instance.ReloadServerData);
        grid.Instance.FilterDefinitions.Should().ContainSingle();

        await grid.InvokeAsync(() => Navigation.NavigateTo(Route));

        await grid.WaitForAssertionAsync(() => grid.Instance.FilterDefinitions.Should().BeEmpty());
        await grid.WaitForAssertionAsync(() => seen[^1].Should().NotContainKey("Name"));
    }

    // Run 18 O-39: a second filter row added on a filtered column has no operator yet. The fetch took
    // that row (the server dropped it, so no filter) while the URL kept the first: both now ignore it.
    [Fact]
    public async Task ColumnFilter_ASecondRowWithNoOperator_DoesNotReplaceTheCompleteRow()
    {
        Navigation.NavigateTo(Route);
        var (grid, _, seen) = RenderGridPage();

        grid.Instance.FilterDefinitions.Add(new FilterDefinition<GridRow> { Column = Column(grid, "Name"), Operator = "contains", Value = "Zed" });
        grid.Instance.FilterDefinitions.Add(new FilterDefinition<GridRow> { Column = Column(grid, "Name"), Operator = null, Value = null });
        await grid.InvokeAsync(grid.Instance.ReloadServerData);

        seen[^1].Should().ContainKey("Name").WhoseValue.Should().Be(("contains", "Zed"));
        State.GetState(Route)!.ColumnFilters["Name"].Should().Be(new ColumnFilterState("contains", "Zed"));
        Uri.UnescapeDataString(new Uri(Navigation.Uri).Query).Should().Contain("c:Name=contains:Zed");
    }

    [Fact]
    public async Task ColumnFilter_OfTwoCompleteRowsOnOneColumn_TheNewestWinsInTheFetchAndTheUrl()
    {
        Navigation.NavigateTo(Route);
        var (grid, _, seen) = RenderGridPage();

        grid.Instance.FilterDefinitions.Add(new FilterDefinition<GridRow> { Column = Column(grid, "Name"), Operator = "contains", Value = "Zed" });
        grid.Instance.FilterDefinitions.Add(new FilterDefinition<GridRow> { Column = Column(grid, "Name"), Operator = "equals", Value = "Ann" });
        await grid.InvokeAsync(grid.Instance.ReloadServerData);

        seen[^1]["Name"].Should().Be(("equals", "Ann"));
        State.GetState(Route)!.ColumnFilters["Name"].Should().Be(new ColumnFilterState("equals", "Ann"));
    }

    // Run 18 O-85 / X-34: the grid's state is a copy taken when its load starts. When the page awaited
    // a slow lookup in OnBeforeFetchAsync, the first-render setup put the restored filter on the grid
    // (clearing the pending flag) during that wait, and the load then read its own empty copy: the
    // restored c: filter was neither sent nor kept in the URL.
    [Fact]
    public async Task ColumnFilterInTheUrl_IsSent_WhenTheSetupAppliesItWhileTheLoadAwaitsALookup()
    {
        Navigation.NavigateTo(Route + "?c:Name=contains%3AAnn");
        var hydrate = JSInterop.SetupModule("./_content/MMCA.Common.UI/nav-interop.js")
            .Setup<ListPageState?>("sessionGet", "mmca.lps:" + Route);
        var (grid, page, seen) = RenderGridPage();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Instance.Gate = gate;

        // The load starts while the setup still waits on its sessionStorage read: the grid holds no filter yet.
        grid.Instance.FilterDefinitions.Should().BeEmpty();
        var load = grid.InvokeAsync(grid.Instance.ReloadServerData);
        hydrate.SetResult(null);

        // The setup applies the filter without rendering, so poll rather than wait for a render.
        for (var attempt = 0; attempt < 200 && grid.Instance.FilterDefinitions.Count == 0; attempt++)
        {
            await Task.Delay(10, Xunit.TestContext.Current.CancellationToken);
        }

        grid.Instance.FilterDefinitions.Should().ContainSingle("the setup put the restored filter on the grid during the load");

        gate.SetResult();
        await load;

        seen[^1].Should().ContainKey("Name").WhoseValue.Should().Be(("contains", "Ann"));
        State.GetState(Route)!.ColumnFilters.Should().ContainKey("Name");
        Uri.UnescapeDataString(new Uri(Navigation.Uri).Query).Should().Contain("c:Name=contains:Ann", "the restored filter stays in the URL");
    }

    // Run 18 A-09 (D10): a same-route navigation (the drawer's My Schedule link on the schedule page,
    // back/forward) restored the filters but reloaded only the grid, so the phone kept the old cards.
    [Fact]
    public async Task SameRouteNavigation_OnAPhone_ReloadsTheCardList()
    {
        Navigation.NavigateTo(Route);
        var fetches = 0;
        var list = RenderCardList(() => fetches++);
        var cut = Render<CardListPage>(ps => ps.Add(p => p.List, list.Instance));
        await cut.InvokeAsync(() => cut.Instance.NotifyBrowserViewportChangeAsync(
            new BrowserViewportEventArgs(Guid.NewGuid(), PhoneViewport, Breakpoint.Xs)));
        var before = fetches;

        await cut.InvokeAsync(() => Navigation.NavigateTo(Route + "?f:mode=mine"));

        await cut.WaitForAssertionAsync(() => fetches.Should().BeGreaterThan(before));
        cut.Instance.Mode.Should().Be("mine");
        State.GetState(Route)!.Filters["mode"].Should().Be("mine", "the phone reload saves, as ReloadActiveLayoutAsync does");
    }

    [Fact]
    public async Task SameRouteNavigation_OnAPhoneWithoutACardListReference_RequestsTheMobileData()
    {
        Navigation.NavigateTo(Route);
        var cut = Render<FilteredPage>();
        await GoToPhoneAsync(cut);
        var before = cut.Instance.MobileDataRequests;

        await cut.InvokeAsync(() => Navigation.NavigateTo(Route + "?f:mode=mine"));

        await cut.WaitForAssertionAsync(() => cut.Instance.MobileDataRequests.Should().BeGreaterThan(before));
    }

    public sealed record GridRow(int Id, string Name);

    private sealed class GridPage : DataGridListPageBase<GridRow>
    {
        [Parameter] public MudDataGrid<GridRow>? Grid { get; set; }

        // Held open to stand in for a slow lookup (ADC's event scope) the fetch awaits.
        public TaskCompletionSource? Gate { get; set; }

        protected override string Title => "Rows";

        protected override MudDataGrid<GridRow>? GridRef => Grid;

        protected override Task OnBeforeFetchAsync(CancellationToken cancellationToken) => Gate?.Task ?? Task.CompletedTask;

        public Task<GridData<GridRow>> LoadAsync(GridState<GridRow> state, List<Dictionary<string, (string Operator, string Value)>> seen) =>
            LoadServerDataAsync(state, (filters, _, _, _, _, _) =>
            {
                seen.Add(new Dictionary<string, (string Operator, string Value)>(filters, StringComparer.Ordinal));
                return Task.FromResult(Result.Success<(IReadOnlyList<GridRow> Items, int TotalItems)>(([], 0)));
            });
    }

    private (IRenderedComponent<MudDataGrid<GridRow>> Grid, IRenderedComponent<GridPage> Page, List<Dictionary<string, (string Operator, string Value)>> Seen) RenderGridPage()
    {
        var seen = new List<Dictionary<string, (string Operator, string Value)>>();
        GridPage? page = null;
        var grid = Render<MudDataGrid<GridRow>>(ps => ps
            .Add(p => p.Filterable, true)
            .Add(p => p.ServerData, (state, _) => page is null
                ? Task.FromResult(new GridData<GridRow> { Items = [], TotalItems = 0 })
                : page.LoadAsync(state, seen))
            .Add(p => p.Columns, builder =>
            {
                builder.OpenComponent<PropertyColumn<GridRow, string>>(0);
                builder.AddAttribute(1, nameof(PropertyColumn<,>.Property), (System.Linq.Expressions.Expression<Func<GridRow, string>>)(row => row.Name));
                builder.CloseComponent();
                builder.OpenComponent<PropertyColumn<GridRow, int>>(2);
                builder.AddAttribute(3, nameof(PropertyColumn<,>.Property), (System.Linq.Expressions.Expression<Func<GridRow, int>>)(row => row.Id));
                builder.CloseComponent();
            }));

        var cut = Render<GridPage>(ps => ps.Add(p => p.Grid, grid.Instance));
        page = cut.Instance;
        return (grid, cut, seen);
    }

    private static Column<GridRow> Column(IRenderedComponent<MudDataGrid<GridRow>> grid, string property) =>
        grid.Instance.RenderedColumns.Single(c => c.PropertyName == property);

    private static Task GoToPhoneAsync(IRenderedComponent<FilteredPage> cut) =>
        cut.InvokeAsync(() => cut.Instance.NotifyBrowserViewportChangeAsync(
            new BrowserViewportEventArgs(Guid.NewGuid(), PhoneViewport, Breakpoint.Xs)));

    private IRenderedComponent<MobileInfiniteScrollList<Row>> RenderCardList(Action onFetch) =>
        RenderUnderTest<MobileInfiniteScrollList<Row>>(p => p
            .Add(c => c.CardTemplate, row => row.Id.ToString(CultureInfo.InvariantCulture))
            .Add(c => c.FetchPageResult, (_, _, _) =>
            {
                onFetch();
                return Task.FromResult(
                    Result.Success<(IReadOnlyList<Row> Items, int TotalItems)>(([new Row(1)], 1)));
            }));
}
