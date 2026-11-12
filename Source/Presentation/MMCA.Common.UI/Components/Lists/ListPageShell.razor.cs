using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Components.Forms;
using MMCA.Common.UI.Resources;
using MudBlazor;

namespace MMCA.Common.UI.Components.Lists;

/// <summary>
/// The markup every admin list page otherwise copies: a <c>PageHeader</c> with the cancel-loading
/// button and the page's header actions, a search row with the page's filters, either a
/// <see cref="MudDataGrid{T}"/> (desktop) or a <see cref="MobileInfiniteScrollList{TItem}"/> (phone),
/// the shared empty and failure content, the pager, and a <see cref="DeleteConfirmation"/>.
/// </summary>
/// <typeparam name="TItem">The row type.</typeparam>
/// <remarks>
/// The shell renders; the page's <c>DataGridListPageBase</c> owns the state. Paging, filter, sort,
/// loading and persistence values all arrive as parameters, and the page reaches the rendered grid,
/// phone list and delete dialog through <see cref="Grid"/>, <see cref="MobileList"/> and
/// <see cref="DeleteDialog"/> (for example <c>protected override MudDataGrid&lt;RoomDTO&gt;? GridRef =&gt; _shell?.Grid;</c>).
/// </remarks>
public partial class ListPageShell<TItem>
{
    private MudDataGrid<TItem>? _grid;

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    /// <summary>Gets or sets the page title rendered in the <c>PageHeader</c>.</summary>
    [Parameter]
    [EditorRequired]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a load is in flight (shows the cancel button and the grid's loading bar).</summary>
    [Parameter] public bool IsLoading { get; set; }

    /// <summary>Gets or sets the callback the cancel-loading button invokes.</summary>
    [Parameter] public EventCallback OnCancelLoading { get; set; }

    /// <summary>Gets or sets the cancel button's visible label; defaults to the shared <c>Common.Button.Cancel</c> text.</summary>
    [Parameter] public string? CancelLabel { get; set; }

    /// <summary>Gets or sets the cancel button's accessible name; defaults to the shared <c>List.Aria.CancelLoading</c> text.</summary>
    [Parameter] public string? CancelAriaLabel { get; set; }

    /// <summary>Gets or sets the page's header actions, rendered inside the header after the cancel button.</summary>
    [Parameter] public RenderFragment? HeaderActions { get; set; }

    /// <summary>Gets or sets a value indicating whether the search box renders. Defaults to <see langword="true"/>.</summary>
    [Parameter] public bool ShowSearch { get; set; } = true;

    /// <summary>Gets or sets the search box text. Defaults to empty.</summary>
    [Parameter] public string? SearchText { get; set; } = string.Empty;

    /// <summary>Gets or sets the callback raised (debounced) when the search text changes.</summary>
    [Parameter] public EventCallback<string> SearchTextChanged { get; set; }

    /// <summary>Gets or sets the search box placeholder; defaults to the shared <c>List.Search.Placeholder</c> text.</summary>
    [Parameter] public string? SearchPlaceholder { get; set; }

    /// <summary>Gets or sets the page's filter controls, rendered in the search row after the search box.</summary>
    [Parameter] public RenderFragment? Filters { get; set; }

    /// <summary>Gets or sets a value indicating whether the phone list renders instead of the grid.</summary>
    [Parameter] public bool IsMobile { get; set; }

    /// <summary>Gets or sets the phone list's page fetcher (see <c>DataGridListPageBase.MobilePageFetcher</c>).</summary>
    [Parameter]
    [EditorRequired]
    public Func<int, int, CancellationToken, Task<Result<(IReadOnlyList<TItem> Items, int TotalItems)>>>? MobileFetch { get; set; }

    /// <summary>Gets or sets the phone list's card body.</summary>
    [Parameter]
    [EditorRequired]
    public RenderFragment<TItem> CardTemplate { get; set; } = default!;

    /// <summary>Gets or sets the callback a phone card click invokes.</summary>
    [Parameter] public EventCallback<TItem> OnCardClick { get; set; }

    /// <summary>Gets or sets the phone list's page size. Defaults to 10.</summary>
    [Parameter] public int MobilePageSize { get; set; } = 10;

    /// <summary>Gets or sets the grid's server data callback (the page's <c>LoadServerDataAsync</c> wrapper).</summary>
    [Parameter]
    [EditorRequired]
    public Func<GridState<TItem>, CancellationToken, Task<GridData<TItem>>>? ServerData { get; set; }

    /// <summary>Gets or sets the grid's zero-based current page (<c>@bind-CurrentPage</c>).</summary>
    [Parameter] public int CurrentPage { get; set; }

    /// <summary>Gets or sets the callback raised when the grid's current page changes.</summary>
    [Parameter] public EventCallback<int> CurrentPageChanged { get; set; }

    /// <summary>Gets or sets the grid's rows per page. Defaults to 10.</summary>
    [Parameter] public int RowsPerPage { get; set; } = 10;

    /// <summary>Gets or sets a value indicating whether the grid renders dense rows. Defaults to <see langword="true"/>.</summary>
    [Parameter] public bool Dense { get; set; } = true;

    /// <summary>Gets or sets the grid's columns.</summary>
    [Parameter]
    [EditorRequired]
    public RenderFragment Columns { get; set; } = default!;

    /// <summary>Gets or sets the pager's page-size options; <see langword="null"/> offers 5, 10, 25, 50 and 100.</summary>
    [Parameter] public IReadOnlyList<int>? PageSizeOptions { get; set; }

    /// <summary>Gets or sets a value indicating whether the last load failed (renders the inline error with Retry).</summary>
    [Parameter] public bool LoadFailed { get; set; }

    /// <summary>Gets or sets a value indicating whether the list is known to be empty (renders the empty message).</summary>
    [Parameter] public bool EmptyConfirmed { get; set; }

    /// <summary>Gets or sets the empty-state text of the grid and the phone list.</summary>
    [Parameter] public string? EmptyMessage { get; set; }

    /// <summary>Gets or sets the Retry callback; with no delegate the retry reloads the grid's server data.</summary>
    [Parameter] public EventCallback OnRetry { get; set; }

    /// <summary>Gets or sets a value indicating whether the delete confirmation renders. Defaults to <see langword="true"/>.</summary>
    [Parameter] public bool ShowDeleteConfirmation { get; set; } = true;

    /// <summary>Gets or sets the localized entity noun the delete confirmation names.</summary>
    [Parameter] public string? DeleteEntityType { get; set; }

    /// <summary>Gets the rendered grid, or <see langword="null"/> on a phone or before the first render.</summary>
    public MudDataGrid<TItem>? Grid => IsMobile ? null : _grid;

    /// <summary>Gets the rendered phone list, or <see langword="null"/> on desktop or before the first render.</summary>
    public MobileInfiniteScrollList<TItem>? MobileList { get => IsMobile ? field : null; private set; }

    /// <summary>Gets the rendered delete confirmation, or <see langword="null"/> when <see cref="ShowDeleteConfirmation"/> is false.</summary>
    public DeleteConfirmation? DeleteDialog { get => ShowDeleteConfirmation ? field : null; private set; }

    private int[] EffectivePageSizeOptions => PageSizeOptions is null ? [5, 10, 25, 50, 100] : [.. PageSizeOptions];

    private Task RetryAsync()
    {
        if (OnRetry.HasDelegate)
        {
            return OnRetry.InvokeAsync();
        }

        return _grid?.ReloadServerData() ?? Task.CompletedTask;
    }
}
