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

        protected override void SaveFilters(Dictionary<string, string> filters) => filters["mode"] = Mode;
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
