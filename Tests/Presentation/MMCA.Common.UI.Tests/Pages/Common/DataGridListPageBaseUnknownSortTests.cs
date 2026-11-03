using System.Linq.Expressions;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Common;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// D7 (Run 15): a restored sort on a column the grid does not render (a stale or hand-edited
/// <c>?s=Bogus</c>) was ignored by the header, but on a grid with no SortDefinition of its own the
/// first-fetch fallback still sent it, so the API answered 400 and the page showed "Could not load
/// this list". An unknown restored column now never reaches the fetch.
/// </summary>
public sealed class DataGridListPageBaseUnknownSortTests : BunitTestBase
{
    public DataGridListPageBaseUnknownSortTests()
    {
        Services.AddSingleton(Mock.Of<IToastService>());
        ConfigureDataGridListPageHost();
    }

    public sealed record WidgetRow(int Id, string Name);

    [Fact]
    public async Task GridWithNoSortDefinition_GivenAnUnknownUrlSort_SendsNoSortColumn()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/widgets?s=Bogus&sd=desc");
        var seenColumns = new List<string?>();
        SortlessGridPage? page = null;
        var grid = Render<MudDataGrid<WidgetRow>>(ps => ps
            .Add(p => p.ServerData, (state, _) => page is null
                ? Task.FromResult(new GridData<WidgetRow> { Items = [], TotalItems = 0 })
                : page.LoadAsync(state, seenColumns))
            .Add(p => p.Columns, builder =>
            {
                // A sortable column with no InitialDirection: the grid has no SortDefinition.
                builder.OpenComponent<PropertyColumn<WidgetRow, string>>(0);
                builder.AddAttribute(1, nameof(PropertyColumn<,>.Property), (Expression<Func<WidgetRow, string>>)(row => row.Name));
                builder.CloseComponent();
            })).Instance;
        grid.SortDefinitions.Should().BeEmpty("the precondition is a grid with no sort of its own");

        var cut = Render<SortlessGridPage>(ps => ps.Add(p => p.Grid, grid));
        page = cut.Instance;
        await cut.InvokeAsync(grid.ReloadServerData);

        seenColumns.Should().NotBeEmpty();
        seenColumns.Should().AllSatisfy(column => column.Should().BeNull("an unknown restored column never reaches the API"));
    }

    [Fact]
    public async Task GridWithNoSortDefinition_GivenAKnownUrlSort_StillSendsIt()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/widgets?s=Name&sd=desc");
        var seenColumns = new List<string?>();
        SortlessGridPage? page = null;
        var grid = Render<MudDataGrid<WidgetRow>>(ps => ps
            .Add(p => p.ServerData, (state, _) => page is null
                ? Task.FromResult(new GridData<WidgetRow> { Items = [], TotalItems = 0 })
                : page.LoadAsync(state, seenColumns))
            .Add(p => p.Columns, builder =>
            {
                builder.OpenComponent<PropertyColumn<WidgetRow, string>>(0);
                builder.AddAttribute(1, nameof(PropertyColumn<,>.Property), (Expression<Func<WidgetRow, string>>)(row => row.Name));
                builder.CloseComponent();
            })).Instance;

        var cut = Render<SortlessGridPage>(ps => ps.Add(p => p.Grid, grid));
        page = cut.Instance;
        await cut.InvokeAsync(grid.ReloadServerData);

        seenColumns.Should().NotBeEmpty();
        seenColumns[^1].Should().Be("Name", "a restored column the grid renders is still honored");
    }

    private sealed class SortlessGridPage : DataGridListPageBase<WidgetRow>
    {
        [Parameter] public MudDataGrid<WidgetRow>? Grid { get; set; }

        protected override string Title => "Widgets";

        protected override MudDataGrid<WidgetRow>? GridRef => Grid;

        public Task<GridData<WidgetRow>> LoadAsync(GridState<WidgetRow> state, List<string?> seenColumns) =>
            LoadServerDataAsync(state, (_, _, _, sortColumn, _, _) =>
            {
                seenColumns.Add(sortColumn);
                return Task.FromResult(Result.Success<(IReadOnlyList<WidgetRow> Items, int TotalItems)>(([], 0)));
            });
    }
}
