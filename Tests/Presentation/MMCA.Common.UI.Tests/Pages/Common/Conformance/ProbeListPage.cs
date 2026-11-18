using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Components.Lists;
using MMCA.Common.UI.Pages.Common;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Pages.Common.Conformance;

/// <summary>A row a <see cref="ProbeListPage"/> pages through.</summary>
/// <param name="Id">The row id, which the detail route carries.</param>
/// <param name="Name">The display text of the first column.</param>
public sealed record ProbeRow(int Id, string Name);

/// <summary>The client service a <see cref="ProbeListPage"/> pages through (a Moq double in the tests).</summary>
public interface IProbeRowClient
{
    /// <summary>Fetches one page of rows, in the shape <c>DataGridListPageBase</c> takes.</summary>
    /// <param name="filters">The filter bag.</param>
    /// <param name="pageNumber">The 1-based page.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="sortColumn">The sort column, if any.</param>
    /// <param name="sortDirection">The sort direction, if any.</param>
    /// <param name="cancellationToken">The fetch's token.</param>
    /// <returns>The page and the total, or the failure the page renders.</returns>
    Task<Result<(IReadOnlyList<ProbeRow> Items, int TotalItems)>> GetPagedAsync(
        Dictionary<string, (string Operator, string Value)> filters,
        int pageNumber,
        int pageSize,
        string? sortColumn,
        string? sortDirection,
        CancellationToken cancellationToken);
}

/// <summary>
/// A consumer-shaped list page wired the way ADR-146 expects: it derives
/// <see cref="DataGridListPageBase{TDto}"/> and renders <see cref="ListPageShell{TItem}"/>, binding
/// the base's loading, paging, failure and empty-state values and a first column that links each
/// row to its detail route through <see cref="RowLinkCell"/>. The virtual member exists only so the
/// failure-proof tests can derive a deliberately broken page; the conforming page keeps its default.
/// </summary>
public class ProbeListPage : DataGridListPageBase<ProbeRow>
{
    /// <summary>The title the page passes to the shell.</summary>
    public const string PageTitle = "Probe rows";

    /// <summary>The text the page renders when the fetch succeeds with no rows.</summary>
    public const string EmptyText = "No probe rows yet.";

    private ListPageShell<ProbeRow>? _shell;

    /// <summary>Gets the detail route of the row with <paramref name="id"/>.</summary>
    /// <param name="id">The row id.</param>
    /// <returns>The route the row's link cell targets.</returns>
    public static string DetailHref(int id) => "/probes/" + id.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override string Title => PageTitle;

    /// <inheritdoc />
    protected override MudDataGrid<ProbeRow>? GridRef => _shell?.Grid;

    /// <summary>
    /// Gets a value indicating whether the page binds the base's <c>LoadFailed</c> flag to the shell.
    /// A broken override leaves it unbound, so a failed fetch renders like an empty list.
    /// </summary>
    protected virtual bool BindsLoadFailed => true;

    [Inject]
    private IProbeRowClient Client { get; set; } = default!;

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ListPageShell<ProbeRow>>(0);
        builder.AddComponentParameter(1, nameof(ListPageShell<>.Title), Title);
        builder.AddComponentParameter(2, nameof(ListPageShell<>.IsLoading), IsLoading);
        builder.AddComponentParameter(3, nameof(ListPageShell<>.OnCancelLoading), EventCallback.Factory.Create(this, CancelLoading));
        builder.AddComponentParameter(4, nameof(ListPageShell<>.IsMobile), IsMobile);
        builder.AddComponentParameter(5, nameof(ListPageShell<>.MobileFetch), MobilePageFetcher(Client.GetPagedAsync));
        builder.AddComponentParameter(6, nameof(ListPageShell<>.ServerData), (Func<GridState<ProbeRow>, CancellationToken, Task<GridData<ProbeRow>>>)LoadServerData);
        builder.AddComponentParameter(7, nameof(ListPageShell<>.CurrentPage), CurrentPageState);
        builder.AddComponentParameter(8, nameof(ListPageShell<>.CurrentPageChanged), EventCallback.Factory.Create<int>(this, page => CurrentPageState = page));
        builder.AddComponentParameter(9, nameof(ListPageShell<>.RowsPerPage), RowsPerPageState);
        builder.AddComponentParameter(10, nameof(ListPageShell<>.LoadFailed), BindsLoadFailed && LoadFailed);
        builder.AddComponentParameter(11, nameof(ListPageShell<>.EmptyConfirmed), EmptyConfirmed);
        builder.AddComponentParameter(12, nameof(ListPageShell<>.EmptyMessage), EmptyText);
        builder.AddComponentParameter(13, nameof(ListPageShell<>.CardTemplate), (RenderFragment<ProbeRow>)(row => card => card.AddContent(0, row.Name)));
        builder.AddComponentParameter(14, nameof(ListPageShell<>.Columns), (RenderFragment)BuildColumns);
        builder.AddComponentReferenceCapture(15, shell => _shell = (ListPageShell<ProbeRow>)shell);
        builder.CloseComponent();
    }

    private static void BuildColumns(RenderTreeBuilder builder)
    {
        builder.OpenComponent<TemplateColumn<ProbeRow>>(0);
        builder.AddComponentParameter(1, nameof(TemplateColumn<>.Title), "Name");
        builder.AddComponentParameter(2, nameof(TemplateColumn<>.Sortable), false);
        builder.AddComponentParameter(3, nameof(TemplateColumn<>.CellTemplate), (RenderFragment<CellContext<ProbeRow>>)(context => cell =>
        {
            cell.OpenComponent<RowLinkCell>(0);
            cell.AddComponentParameter(1, nameof(RowLinkCell.Href), DetailHref(context.Item.Id));
            cell.AddComponentParameter(2, nameof(RowLinkCell.ChildContent), (RenderFragment)(text => text.AddContent(0, context.Item.Name)));
            cell.CloseComponent();
        }));
        builder.CloseComponent();
    }

    private Task<GridData<ProbeRow>> LoadServerData(GridState<ProbeRow> state, CancellationToken cancellationToken) =>
        LoadServerDataAsync(state, Client.GetPagedAsync);
}
