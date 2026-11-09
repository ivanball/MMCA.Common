using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// <c>SaveFilterState</c> is how a page whose phone list fetches through its own delegate persists a
/// filter change: that fetch path saves nothing, so a stale value would otherwise be restored when
/// the user comes back (ADC Run 16, D6).
/// </summary>
public sealed class DataGridListPageBaseFilterStateTests : BunitTestBase
{
    private const string Route = "/things";

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
}
