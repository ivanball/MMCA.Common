using System.Linq.Expressions;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components.Forms;
using MMCA.Common.UI.Components.Lists;
using MMCA.Common.UI.Components.PageState;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components.Lists;

/// <summary>
/// Pins <see cref="ListPageShell{TItem}"/> (item 15): the shell renders the header, search row,
/// grid or mobile list, empty and failure content, pager and delete confirmation that every admin
/// list page used to hand-copy, and holds none of the paging, filter, loading or persistence state:
/// every such value arrives as a parameter. The shell is rendered directly over a static
/// <c>ServerData</c> and <c>MobileFetch</c>; no list-page state service is registered in this
/// class, so a shell that injected one would fail every test here (rule 8).
/// </summary>
public sealed class ListPageShellTests : BunitTestBase
{
    private const string DefaultCancelAria = "Cancel loading";

    private static readonly IReadOnlyList<Row> TwoRows = [new(1, "Alpha"), new(2, "Bravo")];

    public sealed record Row(int Id, string Name);

    private static RenderFragment NameColumn => builder =>
    {
        builder.OpenComponent<PropertyColumn<Row, string>>(0);
        builder.AddAttribute(1, nameof(PropertyColumn<,>.Property), (Expression<Func<Row, string>>)(row => row.Name));
        builder.AddAttribute(2, nameof(PropertyColumn<,>.Title), "Room name");
        builder.CloseComponent();
    };

    // Layout choice (rules 3 and 4)
    [Fact]
    public void Desktop_RendersGrid_NotMobileList_GridRefSet()
    {
        var cut = RenderShell();

        var grid = cut.FindComponents<MudDataGrid<Row>>().Should().ContainSingle().Subject;
        cut.FindComponents<MobileInfiniteScrollList<Row>>().Should().BeEmpty();
        cut.Instance.Grid.Should().BeSameAs(grid.Instance);
        cut.Instance.MobileList.Should().BeNull();
        cut.Find(".page-header .mmca-page-title").TextContent.Should().Be("Rooms", "rule 1: the shell renders PageHeader with its Title");
    }

    [Fact]
    public void Desktop_GridCarriesTheContractConfiguration()
    {
        var cut = RenderShell(p => p
            .Add(s => s.RowsPerPage, 25)
            .Add(s => s.IsLoading, true)
            .Add(s => s.Dense, false));

        var grid = cut.FindComponent<MudDataGrid<Row>>().Instance;
        grid.RowsPerPage.Should().Be(25);
        grid.Loading.Should().BeTrue("Loading follows IsLoading");
        grid.Dense.Should().BeFalse();
        grid.AllowUnsorted.Should().BeFalse();
        grid.Elevation.Should().Be(1);
        grid.Filterable.Should().BeTrue();
        grid.FilterCaseSensitivity.Should().Be(DataGridFilterCaseSensitivity.CaseInsensitive);
        grid.HorizontalScrollbar.Should().BeTrue();
        grid.Hover.Should().BeTrue();
        grid.Striped.Should().BeTrue();
    }

    [Fact]
    public void Desktop_DefaultsToDenseAndTenRowsPerPage()
    {
        var cut = RenderShell();

        var grid = cut.FindComponent<MudDataGrid<Row>>().Instance;
        grid.Dense.Should().BeTrue();
        grid.RowsPerPage.Should().Be(10);
        grid.Loading.Should().BeFalse();
    }

    [Fact]
    public void Mobile_RendersMobileList_NotGrid_MobileListRefSet()
    {
        Func<int, int, CancellationToken, Task<Result<(IReadOnlyList<Row> Items, int TotalItems)>>> fetch = MobileFetch;

        var cut = RenderShell(
            p => p
                .Add(s => s.IsMobile, true)
                .Add(s => s.MobilePageSize, 4)
                .Add(s => s.EmptyMessage, "No rooms yet"),
            mobileFetch: fetch);

        var list = cut.FindComponents<MobileInfiniteScrollList<Row>>().Should().ContainSingle().Subject;
        cut.FindComponents<MudDataGrid<Row>>().Should().BeEmpty();
        cut.Instance.MobileList.Should().BeSameAs(list.Instance);
        cut.Instance.Grid.Should().BeNull();
        list.Instance.FetchPageResult.Should().BeSameAs(fetch);
        list.Instance.PageSize.Should().Be(4);
        list.Instance.EmptyMessage.Should().Be("No rooms yet");
        cut.WaitForAssertion(() => cut.FindAll(".probe-card").Should().HaveCount(2, "the CardTemplate renders each fetched row"));
    }

    [Fact]
    public void Mobile_CardClick_InvokesOnCardClick()
    {
        Row? clicked = null;

        var cut = RenderShell(p => p
            .Add(s => s.IsMobile, true)
            .Add(s => s.OnCardClick, (Row row) => clicked = row));

        cut.WaitForAssertion(() => cut.FindAll(".mobile-list-card").Should().HaveCount(2));
        cut.FindAll(".mobile-list-card")[1].Click();

        clicked.Should().Be(TwoRows[1]);
    }

    // Header (rule 1)
    [Fact]
    public void IsLoading_ShowsCancelButtonWithAriaLabel_ClickInvokesOnCancelLoading()
    {
        var cancelled = 0;

        var cut = RenderShell(p => p
            .Add(s => s.IsLoading, true)
            .Add(s => s.OnCancelLoading, () => cancelled++));

        var button = cut.Find($".page-header button[aria-label='{DefaultCancelAria}']");
        button.TextContent.Should().Contain("Cancel", "the visible label defaults to Common.Button.Cancel");
        var mudButton = cut.FindComponents<MudButton>().Single(b => b.Instance.Color == Color.Error);
        mudButton.Instance.StartIcon.Should().Be(Icons.Material.Filled.Cancel);

        button.Click();

        cancelled.Should().Be(1);
    }

    [Fact]
    public void IsLoading_CancelLabelAndAriaLabel_CanBeOverridden()
    {
        var cut = RenderShell(p => p
            .Add(s => s.IsLoading, true)
            .Add(s => s.CancelLabel, "Stop")
            .Add(s => s.CancelAriaLabel, "Stop loading rooms"));

        var button = cut.Find(".page-header button[aria-label='Stop loading rooms']");
        button.TextContent.Should().Contain("Stop");
        cut.FindAll($"button[aria-label='{DefaultCancelAria}']").Should().BeEmpty();
    }

    [Fact]
    public void NotLoading_NoCancelButton()
    {
        var cut = RenderShell(p => p.Add(s => s.IsLoading, false));

        cut.FindAll($"button[aria-label='{DefaultCancelAria}']").Should().BeEmpty();
        cut.FindComponents<MudButton>().Should().NotContain(b => b.Instance.Color == Color.Error);
    }

    [Fact]
    public void HeaderActions_RenderInsideHeader()
    {
        var cut = RenderShell(p => p
            .Add(s => s.IsLoading, true)
            .Add(s => s.HeaderActions, "<button id=\"probe-action\" type=\"button\">New room</button>"));

        var headerButtons = cut.FindAll(".page-header button");
        headerButtons.Select(b => b.Id).Should().Contain("probe-action", "HeaderActions render inside the PageHeader");
        var cancelIndex = headerButtons.ToList().FindIndex(b => b.GetAttribute("aria-label") == DefaultCancelAria);
        var actionIndex = headerButtons.ToList().FindIndex(b => b.Id == "probe-action");
        cancelIndex.Should().BeGreaterThanOrEqualTo(0);
        actionIndex.Should().BeGreaterThan(cancelIndex, "HeaderActions come after the cancel button");
    }

    // Search row (rule 2)
    [Fact]
    public async Task Search_TypingInvokesSearchTextChanged()
    {
        string? typed = null;

        var cut = RenderShell(p => p.Add(s => s.SearchTextChanged, (string value) => typed = value));

        await cut.Find(".list-search-field input").InputAsync(new ChangeEventArgs { Value = "bra" });

        // DebounceInterval is 300 ms, so the callback arrives after the debounce, not on the keystroke.
        await cut.WaitForAssertionAsync(() => typed.Should().Be("bra"), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SearchField_HasListSearchFieldClass()
    {
        var cut = RenderShell();

        cut.FindAll(".list-search-field").Should().NotBeEmpty();
        cut.FindComponent<MudTextField<string>>().Instance.Class.Should().Be("list-search-field");
    }

    [Fact]
    public void SearchField_BindsSearchTextAndCarriesTheContractConfiguration()
    {
        var cut = RenderShell(p => p.Add(s => s.SearchText, "alp"));

        var field = cut.FindComponent<MudTextField<string>>().Instance;
        field.Value.Should().Be("alp");
        field.Placeholder.Should().Be("Search", "the placeholder defaults to List.Search.Placeholder");
        field.Adornment.Should().Be(Adornment.Start);
        field.AdornmentIcon.Should().Be(Icons.Material.Filled.Search);
        field.Immediate.Should().BeTrue();
        field.DebounceInterval.Should().Be(300);
        field.Clearable.Should().BeTrue();
        field.Variant.Should().Be(Variant.Outlined);
    }

    [Fact]
    public void SearchPlaceholder_CanBeOverridden()
    {
        var cut = RenderShell(p => p.Add(s => s.SearchPlaceholder, "Find a room"));

        cut.FindComponent<MudTextField<string>>().Instance.Placeholder.Should().Be("Find a room");
    }

    [Fact]
    public void ShowSearchFalse_NoSearchField()
    {
        var cut = RenderShell(p => p.Add(s => s.ShowSearch, false));

        cut.FindAll(".list-search-field").Should().BeEmpty();
        cut.FindComponents<MudTextField<string>>().Should().BeEmpty();
        SearchRows(cut).Should().BeEmpty("with no search and no Filters there is no search row at all");
    }

    [Fact]
    public void Filters_RenderInSearchRow()
    {
        var cut = RenderShell(p => p.Add(s => s.Filters, "<span id=\"probe-filter\">Event picker</span>"));

        var row = SearchRows(cut).Should().ContainSingle().Subject;
        row.Instance.Row.Should().BeTrue();
        row.Instance.Wrap.Should().Be(Wrap.Wrap);
        row.Instance.AlignItems.Should().Be(AlignItems.Center);
        row.Instance.Spacing.Should().Be(2);

        var markup = row.Markup;
        markup.Should().Contain("probe-filter").And.Contain("list-search-field");
        markup.IndexOf("list-search-field", StringComparison.Ordinal)
            .Should().BeLessThan(markup.IndexOf("probe-filter", StringComparison.Ordinal), "Filters follow the search field");
    }

    [Fact]
    public void Filters_WithoutSearch_StillRenderTheRow()
    {
        var cut = RenderShell(p => p
            .Add(s => s.ShowSearch, false)
            .Add(s => s.Filters, "<span id=\"probe-filter\">Status picker</span>"));

        var row = SearchRows(cut).Should().ContainSingle().Subject;
        row.Markup.Should().Contain("probe-filter");
        cut.FindAll(".list-search-field").Should().BeEmpty();
    }

    // Grid content (rules 4, 5, 6)
    [Fact]
    public void Columns_RegisterWithGrid_HeaderTitlesRendered()
    {
        var cut = RenderShell();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".mud-table-head").TextContent.Should().Contain("Room name");
            cut.Markup.Should().Contain("Alpha").And.Contain("Bravo");
        });
    }

    [Fact]
    public async Task ServerData_CalledWithGridState()
    {
        var states = new List<GridState<Row>>();

        var cut = RenderShell(
            p => p.Add(s => s.RowsPerPage, 25),
            serverData: (state, _) =>
            {
                states.Add(state);
                return Task.FromResult(new GridData<Row> { Items = TwoRows, TotalItems = TwoRows.Count });
            });

        await cut.WaitForAssertionAsync(() => states.Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        states[^1].Page.Should().Be(0);
        states[^1].PageSize.Should().Be(25);
    }

    [Fact]
    public async Task CurrentPageChanged_RaisedWhenPagerMoves()
    {
        var raised = new List<int>();
        var firstPage = Enumerable.Range(1, 10).Select(i => new Row(i, "Room")).ToList();

        var cut = RenderShell(
            p => p.Add(s => s.CurrentPageChanged, (int page) => raised.Add(page)),
            serverData: (_, _) => Task.FromResult(new GridData<Row> { Items = firstPage, TotalItems = 30 }));
        await cut.WaitForAssertionAsync(() => cut.FindAll("button[aria-label='Next page']").Should().NotBeEmpty(), TimeSpan.FromSeconds(5));

        await cut.Find("button[aria-label='Next page']").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => raised.Should().Contain(1), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LoadFailed_RendersRetry_WithoutOnRetry_ReloadsGrid()
    {
        var calls = 0;

        var cut = RenderShell(
            p => p.Add(s => s.LoadFailed, true),
            serverData: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new GridData<Row> { Items = [], TotalItems = 0 });
            });
        await cut.WaitForAssertionAsync(() => cut.FindButtonByText("Retry"), TimeSpan.FromSeconds(5));
        cut.FindComponent<ListNoRecordsContent>().Instance.LoadFailed.Should().BeTrue();
        var before = Volatile.Read(ref calls);

        await cut.FindButtonByText("Retry").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(
            () => Volatile.Read(ref calls).Should().BeGreaterThan(before, "with no OnRetry delegate the retry reloads the grid's server data"),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LoadFailed_WithOnRetry_InvokesOnRetry()
    {
        var retried = 0;

        var cut = RenderShell(
            p => p
                .Add(s => s.LoadFailed, true)
                .Add(s => s.OnRetry, () => retried++),
            serverData: (_, _) => Task.FromResult(new GridData<Row> { Items = [], TotalItems = 0 }));
        await cut.WaitForAssertionAsync(() => cut.FindButtonByText("Retry"), TimeSpan.FromSeconds(5));

        await cut.FindButtonByText("Retry").ClickAsync(new MouseEventArgs());

        retried.Should().Be(1);
    }

    [Fact]
    public async Task EmptyConfirmed_RendersEmptyMessage()
    {
        var cut = RenderShell(
            p => p
                .Add(s => s.EmptyConfirmed, true)
                .Add(s => s.EmptyMessage, "No rooms yet"),
            serverData: (_, _) => Task.FromResult(new GridData<Row> { Items = [], TotalItems = 0 }));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("No rooms yet"), TimeSpan.FromSeconds(5));
        var content = cut.FindComponent<ListNoRecordsContent>().Instance;
        content.EmptyConfirmed.Should().BeTrue();
        content.EmptyMessage.Should().Be("No rooms yet");
        content.LoadFailed.Should().BeFalse();
    }

    [Fact]
    public async Task EmptyNotConfirmed_RendersNoEmptyMessage()
    {
        var cut = RenderShell(
            p => p
                .Add(s => s.EmptyConfirmed, false)
                .Add(s => s.EmptyMessage, "No rooms yet"),
            serverData: (_, _) => Task.FromResult(new GridData<Row> { Items = [], TotalItems = 0 }));

        await cut.WaitForAssertionAsync(() => cut.FindComponents<ListNoRecordsContent>().Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        cut.FindComponent<ListNoRecordsContent>().Instance.EmptyConfirmed.Should().BeFalse();
        cut.Markup.Should().NotContain("No rooms yet");
    }

    [Fact]
    public async Task PageSizeOptions_DefaultFiveValues()
    {
        var cut = RenderShell();

        await cut.WaitForAssertionAsync(() => cut.FindComponents<MudDataGridPager<Row>>().Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        cut.FindComponent<MudDataGridPager<Row>>().Instance.PageSizeOptions.Should().Equal(5, 10, 25, 50, 100);

        // The innermost MudStack holding the pager is the PagerContent row.
        var pagerRow = cut.FindComponents<MudStack>()
            .Last(s => s.FindComponents<MudDataGridPager<Row>>().Count > 0);
        pagerRow.Instance.Row.Should().BeTrue();
        pagerRow.Instance.AlignItems.Should().Be(AlignItems.Center);
    }

    [Fact]
    public async Task PageSizeOptions_Custom_ReachThePager()
    {
        var cut = RenderShell(p => p
            .Add(s => s.RowsPerPage, 20)
            .Add(s => s.PageSizeOptions, (IReadOnlyList<int>)[20, 40]));

        await cut.WaitForAssertionAsync(() => cut.FindComponents<MudDataGridPager<Row>>().Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        cut.FindComponent<MudDataGridPager<Row>>().Instance.PageSizeOptions.Should().Equal(20, 40);
    }

    // Delete confirmation (rule 7)
    [Fact]
    public void DeleteDialog_RenderedAndExposed()
    {
        var cut = RenderShell(p => p.Add(s => s.DeleteEntityType, "room"));

        var dialog = cut.FindComponents<DeleteConfirmation>().Should().ContainSingle().Subject;
        dialog.Instance.EntityType.Should().Be("room");
        cut.Instance.DeleteDialog.Should().BeSameAs(dialog.Instance);
    }

    [Fact]
    public void DeleteDialog_AlsoRenderedForTheMobileList()
    {
        var cut = RenderShell(p => p
            .Add(s => s.IsMobile, true)
            .Add(s => s.DeleteEntityType, "room"));

        cut.Instance.DeleteDialog.Should().BeSameAs(cut.FindComponent<DeleteConfirmation>().Instance);
    }

    [Fact]
    public void ShowDeleteConfirmationFalse_DeleteDialogNull()
    {
        var cut = RenderShell(p => p.Add(s => s.ShowDeleteConfirmation, false));

        cut.FindComponents<DeleteConfirmation>().Should().BeEmpty();
        cut.Instance.DeleteDialog.Should().BeNull();
    }

    // Stateless shell (rule 8)
    [Fact]
    public async Task ParameterChanges_AreWhatTheGridAndHeaderFollow()
    {
        var tenRows = Enumerable.Range(1, 10).Select(i => new Row(i, "Room")).ToList();
        var cut = RenderShell(
            p => p
                .Add(s => s.IsLoading, false)
                .Add(s => s.CurrentPage, 0),
            serverData: (_, _) => Task.FromResult(new GridData<Row> { Items = tenRows, TotalItems = 30 }));
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Room"), TimeSpan.FromSeconds(5));

        cut.Render(p => p
            .Add(s => s.IsLoading, true)
            .Add(s => s.CurrentPage, 1));

        var grid = cut.FindComponent<MudDataGrid<Row>>().Instance;
        grid.Loading.Should().BeTrue("loading state arrives as a parameter, not from the shell");
        grid.CurrentPage.Should().Be(1, "paging state arrives as a parameter, not from the shell");
        cut.FindAll($"button[aria-label='{DefaultCancelAria}']").Should().ContainSingle();

        cut.Render(p => p.Add(s => s.IsLoading, false));

        cut.FindComponent<MudDataGrid<Row>>().Instance.Loading.Should().BeFalse();
        cut.FindAll($"button[aria-label='{DefaultCancelAria}']").Should().BeEmpty();
    }

    private static Task<Result<(IReadOnlyList<Row> Items, int TotalItems)>> MobileFetch(int page, int pageSize, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success<(IReadOnlyList<Row>, int)>((page == 1 ? TwoRows : [], TwoRows.Count)));

    private static readonly Func<int, int, CancellationToken, Task<Result<(IReadOnlyList<Row> Items, int TotalItems)>>> DefaultMobileFetch = MobileFetch;

    private static Func<GridState<Row>, CancellationToken, Task<GridData<Row>>> StaticServerData =>
        (_, _) => Task.FromResult(new GridData<Row> { Items = TwoRows, TotalItems = TwoRows.Count });

    private static List<IRenderedComponent<MudStack>> SearchRows(IRenderedComponent<ListPageShell<Row>> cut) =>
        [.. cut.FindComponents<MudStack>().Where(s => s.Instance.Class == "mb-2")];

    private IRenderedComponent<ListPageShell<Row>> RenderShell(
        Action<ComponentParameterCollectionBuilder<ListPageShell<Row>>>? configure = null,
        Func<GridState<Row>, CancellationToken, Task<GridData<Row>>>? serverData = null,
        Func<int, int, CancellationToken, Task<Result<(IReadOnlyList<Row> Items, int TotalItems)>>>? mobileFetch = null)
    {
        return RenderUnderTest<ListPageShell<Row>>(p =>
        {
            p.Add(s => s.Title, "Rooms")
                .Add(s => s.ServerData, serverData ?? StaticServerData)
                .Add(s => s.MobileFetch, mobileFetch ?? DefaultMobileFetch)
                .Add(s => s.CardTemplate, row => $"<span class=\"probe-card\">{row.Name}</span>")
                .Add(s => s.Columns, NameColumn);
            configure?.Invoke(p);
        });
    }
}
