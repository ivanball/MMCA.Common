using System.Text.Json;
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
/// The prerender state <see cref="DataGridListPageBase{TDto}"/> hands to the interactive pass. On a
/// Blazor Server circuit the browser returns that state inside the circuit-start SignalR message, and a
/// message over the hub's 32 KB receive limit closes the connection, so the page reconnects forever and
/// never becomes interactive. A last grid page whose JSON is over the budget must persist nothing (the
/// interactive pass refetches); one under it persists and restores as before.
/// </summary>
public sealed class DataGridListPageBasePrerenderStateTests : BunitTestBase
{
    private static readonly string PersistKey = $"grid:{typeof(TestGridPage).FullName}";

    private readonly BunitPersistentComponentState _state;

    public DataGridListPageBasePrerenderStateTests()
    {
        Services.AddSingleton(new Mock<IToastService>().Object);

        // The ConfigureDataGridListPageHost block, spelled out because these tests drive the persistent
        // state double that helper creates and discards. SetRendererInfo stays LAST: it freezes the
        // service provider.
        Services.TryAddScoped<ListPageStateService>();
        Services.TryAddScoped<ListPageQueryStateService>();
        Services.AddSingleton(Mock.Of<IBrowserViewportService>());
        _state = AddBunitPersistentComponentState();
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    public sealed record WidgetRow(int Id, string Name);

    private sealed class TestGridPage : DataGridListPageBase<WidgetRow>
    {
        public int Fetches { get; private set; }

        public List<WidgetRow> Rows { get; set; } = [];

        protected override string Title => "Widgets";

        public Task<GridData<WidgetRow>> LoadAsync() =>
            LoadServerDataAsync(new GridState<WidgetRow> { Page = 0, PageSize = 100 }, FetchAsync);

        private Task<Result<(IReadOnlyList<WidgetRow> Items, int TotalItems)>> FetchAsync(
            Dictionary<string, (string Operator, string Value)> filters,
            int pageNumber,
            int pageSize,
            string? sortColumn,
            string? sortDirection,
            CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(Result.Success<(IReadOnlyList<WidgetRow> Items, int TotalItems)>((Rows, Rows.Count + 1000)));
        }
    }

    [Fact]
    public async Task Persisting_WhenTheLastPageSerializesOverTheBudget_PersistsNothingUnderTheGridKey()
    {
        // 100 rows of about 300 bytes each: roughly 30 KB of JSON, the shape of a wide admin grid at a
        // large page size, and well over the budget that keeps the circuit-start message under 32 KB.
        var rows = Enumerable.Range(1, 100).Select(i => new WidgetRow(i, new string('x', 300))).ToList();
        var cut = Render<TestGridPage>();
        cut.Instance.Rows = rows;
        await cut.InvokeAsync(cut.Instance.LoadAsync);

        _state.TriggerOnPersisting();

        _state.TryTake<JsonElement>(PersistKey, out _).Should().BeFalse(
            "an over-budget snapshot would overflow the circuit-start message; the interactive pass refetches instead");
    }

    [Fact]
    public async Task Persisting_WhenTheLastPageIsWithinTheBudget_PersistsTheRowsAndTotal()
    {
        var rows = new List<WidgetRow> { new(1, "Alpha"), new(2, "Beta"), new(3, "Gamma") };
        var cut = Render<TestGridPage>();
        cut.Instance.Rows = rows;
        await cut.InvokeAsync(cut.Instance.LoadAsync);

        _state.TriggerOnPersisting();

        _state.TryTake<JsonElement>(PersistKey, out var persisted).Should().BeTrue();
        persisted.GetProperty("totalItems").GetInt32().Should().Be(1003);
        persisted.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .Should().Equal("Alpha", "Beta", "Gamma");
    }

    [Fact]
    public async Task FirstInteractiveLoad_AfterAWithinBudgetPersist_RestoresTheRowsWithoutFetching()
    {
        var prerendered = Render<TestGridPage>();
        prerendered.Instance.Rows = [new(1, "Alpha"), new(2, "Beta")];
        await prerendered.InvokeAsync(prerendered.Instance.LoadAsync);
        _state.TriggerOnPersisting();

        var interactive = Render<TestGridPage>();
        var data = await interactive.InvokeAsync(interactive.Instance.LoadAsync);

        interactive.Instance.Fetches.Should().Be(0, "the state the prerender pass persisted answers the first interactive load");
        data.TotalItems.Should().Be(1002);
        data.Items.Should().Equal(new WidgetRow(1, "Alpha"), new WidgetRow(2, "Beta"));
    }

    [Fact]
    public async Task FirstInteractiveLoad_WithPersistedStateWithinTheBudget_ReturnsItWithoutFetching()
    {
        _state.Persist(PersistKey, new { Items = new[] { new WidgetRow(7, "Restored") }, TotalItems = 42 });

        var cut = Render<TestGridPage>();
        var data = await cut.InvokeAsync(cut.Instance.LoadAsync);

        cut.Instance.Fetches.Should().Be(0, "the restored prerender page answers the first interactive load");
        data.TotalItems.Should().Be(42);
        data.Items.Should().Equal(new WidgetRow(7, "Restored"));
    }
}
