using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Common;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// Pins that a list page's search box and a column filter on the SAME property both reach the fetch
/// (grid-filter audit, run 9, item B). Pages inject the search term through the
/// <c>additionalFilters</c> callback under the column's own key (for example
/// <c>filters["Name"] = ("contains", search)</c>), which used to OVERWRITE the column's own operator and
/// value, so a grid filtered on <c>Name equals "Blue Shirt"</c> with "blue" in the search box silently
/// became <c>Name contains "blue"</c>.
/// <para>
/// Request shape chosen deliberately: the fetch delegate's filter dictionary is the ONLY channel the
/// base class has to the server (its shape mirrors <c>IEntityService.GetPagedAsync</c>, which pages pass
/// as a method group), so "both reach the server" is asserted as "both (operator, value) pairs are in
/// the dictionary the fetch receives". The tests do NOT prescribe which key carries the search term:
/// the column keeps its own key here only because that is what the server already resolves, and the
/// search term may travel under any key the server maps back to the same property.
/// </para>
/// </summary>
public sealed class DataGridListPageBaseSearchFilterTests : BunitTestBase
{
    public DataGridListPageBaseSearchFilterTests()
    {
        Services.AddSingleton(new Mock<IToastService>().Object);
        ConfigureDataGridListPageHost();
    }

    // Public so Moq can proxy MudBlazor's Column<SearchRow> over it.
    public sealed record SearchRow(int Id, string Name);

    private sealed class SearchGridPage : DataGridListPageBase<SearchRow>
    {
        public Dictionary<string, (string Operator, string Value)>? SeenFilters { get; private set; }

        protected override string Title => "Rows";

        public Task<GridData<SearchRow>> LoadAsync(
            GridState<SearchRow> state,
            Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters) =>
            LoadServerDataAsync(state, CaptureAsync, additionalFilters);

        public Task<GridData<SearchRow>> LoadVirtualizedAsync(
            GridStateVirtualize<SearchRow> state,
            Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters) =>
            LoadVirtualizedServerDataAsync(state, CaptureAsync, additionalFilters);

        private Task<Result<(IReadOnlyList<SearchRow> Items, int TotalItems)>> CaptureAsync(
            Dictionary<string, (string Operator, string Value)> filters,
            int pageNumber,
            int pageSize,
            string? sortColumn,
            string? sortDirection,
            CancellationToken cancellationToken)
        {
            SeenFilters = new Dictionary<string, (string Operator, string Value)>(filters, StringComparer.Ordinal);
            return Task.FromResult(Result.Success<(IReadOnlyList<SearchRow> Items, int TotalItems)>(([], 0)));
        }
    }

    private static IFilterDefinition<SearchRow> ColumnFilter(string propertyName, string @operator, string value)
    {
        var column = new Mock<Column<SearchRow>>();
        column.Setup(c => c.PropertyName).Returns(propertyName);

        var definition = new Mock<IFilterDefinition<SearchRow>>();
        definition.SetupGet(f => f.Column).Returns(column.Object);
        definition.SetupGet(f => f.Operator).Returns(@operator);
        definition.SetupGet(f => f.Value).Returns(value);
        return definition.Object;
    }

    /// <summary>The search box, injected the way list pages do it: under the column's own property name.</summary>
    private static void SearchOnName(Dictionary<string, (string Operator, string Value)> filters) =>
        filters["Name"] = ("contains", "blue");

    [Fact]
    public async Task LoadServerDataAsync_SearchOnAFilteredColumn_KeepsTheColumnFilterAndTheSearch()
    {
        var cut = Render<SearchGridPage>();
        var state = new GridState<SearchRow> { Page = 0, PageSize = 10, FilterDefinitions = [ColumnFilter("Name", "equals", "Blue Shirt")] };

        await cut.InvokeAsync(() => cut.Instance.LoadAsync(state, SearchOnName));

        var sent = cut.Instance.SeenFilters!.Values;
        sent.Should().Contain(("equals", "Blue Shirt"),
            "the column's own operator and value must not be overwritten by the search box");
        sent.Should().Contain(("contains", "blue"), "the search term must still reach the server");
    }

    [Fact]
    public async Task LoadVirtualizedServerDataAsync_SearchOnAFilteredColumn_KeepsTheColumnFilterAndTheSearch()
    {
        var cut = Render<SearchGridPage>();
        var state = new GridStateVirtualize<SearchRow> { StartIndex = 0, Count = 10, FilterDefinitions = [ColumnFilter("Name", "starts with", "Blu")] };

        await cut.InvokeAsync(() => cut.Instance.LoadVirtualizedAsync(state, SearchOnName));

        var sent = cut.Instance.SeenFilters!.Values;
        sent.Should().Contain(("starts with", "Blu"));
        sent.Should().Contain(("contains", "blue"));
    }

    [Fact]
    public async Task LoadServerDataAsync_SearchOnAFilteredColumn_LeavesOtherColumnsUntouched()
    {
        var cut = Render<SearchGridPage>();
        var state = new GridState<SearchRow>
        {
            Page = 0,
            PageSize = 10,
            FilterDefinitions =
            [
                ColumnFilter("Name", "is not empty", string.Empty),
                ColumnFilter("Id", ">", "3"),
            ],
        };

        await cut.InvokeAsync(() => cut.Instance.LoadAsync(state, SearchOnName));

        var sent = cut.Instance.SeenFilters!;
        sent.Should().ContainKey("Id").WhoseValue.Should().Be((">", "3"));
        sent.Values.Should().Contain(("is not empty", string.Empty));
        sent.Values.Should().Contain(("contains", "blue"));
    }

    [Fact]
    public async Task LoadServerDataAsync_SearchWithNoColumnFilter_StillArrives()
    {
        // Regression guard: with nothing to collide with, the search term still arrives.
        var cut = Render<SearchGridPage>();

        await cut.InvokeAsync(() => cut.Instance.LoadAsync(new GridState<SearchRow> { Page = 0, PageSize = 10 }, SearchOnName));

        cut.Instance.SeenFilters!.Values.Should().ContainSingle().Which.Should().Be(("contains", "blue"));
    }
}
