using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Services;
using MudBlazor;
using MudBlazor.Services;

namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// Base class for list pages using <see cref="MudDataGrid{T}"/> with server-side paging.
/// Encapsulates the common CTS management, loading state, filter/sort extraction from
/// <see cref="GridState{T}"/>, error handling, mobile/desktop viewport detection, and
/// <see cref="IAsyncDisposable"/> pattern that is otherwise repeated across every list page.
/// </summary>
/// <typeparam name="TDto">The DTO type displayed in the grid.</typeparam>
public abstract class DataGridListPageBase<TDto> : ComponentBase, IBrowserViewportObserver, IAsyncDisposable, IDisposable
{
    [Inject] protected ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IBrowserViewportService BrowserViewportService { get; set; } = default!;
    [Inject] private ListPageStateService ListPageStateService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    protected bool IsLoading { get; private set; }
    protected abstract string Title { get; }

    /// <summary>True when the viewport is below the sidebar-collapse threshold (phone or tablet, &lt; 960 px).</summary>
    protected bool IsMobile { get; private set; }

    // ── Mobile card-view state ──
    protected IReadOnlyList<TDto> MobileItems { get; private set; } = [];
    protected int MobileTotalItems { get; private set; }
    protected int MobileCurrentPage { get; set; } = 1;
    protected int MobilePageSize { get; set; } = 10;

    /// <summary>
    /// Current page for the MudDataGrid (0-indexed). Bind in Razor via
    /// <c>@bind-CurrentPage="CurrentPageState"</c>. Restored from saved state on initialization,
    /// so the grid's first <c>ServerData</c> call fetches the correct page directly.
    /// </summary>
    protected int CurrentPageState { get; set; }

    /// <summary>
    /// Rows per page to pass to the MudDataGrid. Bind in Razor via
    /// <c>RowsPerPage="@RowsPerPageState"</c> (one-way; the grid's pager owns updates after
    /// first render). Restored from saved state on initialization so the pager's
    /// <c>OnInitializedAsync</c> sees a non-null <c>_rowsPerPage</c> and skips its
    /// <c>PageSizeOptions.FirstOrDefault()</c> fallback. Defaults to 10 to match MudDataGrid v9's
    /// own default.
    /// </summary>
    protected int RowsPerPageState { get; set; } = 10;

    private CancellationTokenSource? _cts;
    private bool _disposed;
    private IJSObjectReference? _scrollModule;
    private DotNetObjectReference<DataGridListPageBase<TDto>>? _dotNetRef;
    private double? _pendingScrollRestore;
    private int _savedPage;
    private int _savedPageSize;
    private readonly string _scrollTrackerId = Guid.NewGuid().ToString();

    /// <inheritdoc />
    public Guid Id { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public ResizeOptions ResizeOptions { get; } = new() { ReportRate = 250 };

    /// <summary>Override in derived pages to save page-specific filter/search values (e.g., search string, status dropdown).</summary>
    protected virtual void SaveFilters(Dictionary<string, string> filters) { }

    /// <summary>Override in derived pages to restore page-specific filter/search values from saved state.</summary>
    protected virtual void RestoreFilters(IReadOnlyDictionary<string, string> filters) { }

    /// <summary>
    /// Override in derived pages to expose the <see cref="MudDataGrid{T}"/> component reference
    /// (typically a <c>private MudDataGrid&lt;TDto&gt;? _dataGrid;</c> field captured via
    /// <c>@ref="_dataGrid"</c> in the Razor markup). The base class needs this to programmatically
    /// restore <c>RowsPerPage</c> after first render — see <see cref="OnAfterRenderAsync"/>.
    /// Returns <see langword="null"/> by default for pages that don't need rows-per-page restoration
    /// (e.g., mobile-only pages).
    /// </summary>
    protected virtual MudDataGrid<TDto>? GridRef => null;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        var state = ListPageStateService.GetState(GetRoutePath());
        if (state is not null)
        {
            CurrentPageState = state.Page;
            _savedPage = state.Page;
            _savedPageSize = state.PageSize;
            if (state.PageSize > 0)
            {
                RowsPerPageState = state.PageSize;
            }

            MobileCurrentPage = state.MobilePage;
            RestoreFilters(state.Filters);

            if (state.ScrollPosition > 0)
            {
                _pendingScrollRestore = state.ScrollPosition;
            }
        }

        base.OnInitialized();
    }

    /// <inheritdoc />
    public async Task NotifyBrowserViewportChangeAsync(BrowserViewportEventArgs browserViewportEventArgs) =>
        await InvokeAsync(async () =>
        {
            var wasMobile = IsMobile;
            IsMobile = BreakpointConstants.IsMobileBreakpoint(browserViewportEventArgs.Breakpoint);

            if (IsMobile && !wasMobile)
            {
                MobileCurrentPage = 1;
                await OnMobileDataRequestedAsync();
            }

            StateHasChanged();
        });

    /// <summary>
    /// Subscribes to viewport changes after the first render (JS interop requires a rendered DOM),
    /// imports the scroll-tracking JS module, restores rows-per-page (which MudDataGrid v9
    /// cannot accept via parameter without resetting <c>CurrentPage</c>), and restores any
    /// pending scroll position once the grid has rendered its rows.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await BrowserViewportService.SubscribeAsync(this, fireImmediately: true);

            _scrollModule = await JS.InvokeAsync<IJSObjectReference>(
                "import",
                "./_content/MMCA.Common.UI/list-page-scroll.js");
            _dotNetRef = DotNetObjectReference.Create(this);
            await _scrollModule.InvokeVoidAsync(
                "enableScrollTracking",
                _dotNetRef,
                _scrollTrackerId,
                150);

            // SAFETY NET: even though we pass RowsPerPage as a parameter (so the pager init sees
            // the saved size), MudDataGrid v9's parameter setter is one-shot and queues an
            // InvokeAsync that may not propagate the value to _rowsPerPage in time for the first
            // fetch on every render path. If the grid's actual RowsPerPage doesn't match what we
            // restored, force it now via the public method with resetPage: false (to preserve
            // CurrentPage). The early-return guard inside SetRowsPerPageAsync makes this a no-op
            // when the parameter approach already worked.
            if (_savedPageSize > 0 && GridRef is { } sizeGrid && sizeGrid.RowsPerPage != _savedPageSize)
            {
                await sizeGrid.SetRowsPerPageAsync(_savedPageSize, resetPage: false);
            }

            // The buggy RowsPerPage parameter setter in MudDataGrid v9 (it always uses
            // resetPage: true) clobbers CurrentPage to 0 as a side effect when CurrentPage was
            // non-zero. We re-restore CurrentPage here using the cached _savedPage so
            // page-number restoration still works alongside rows-per-page restoration.
            RestoreCurrentPageAfterRowsPerPageReset();
        }

        // Restore scroll only after the grid has finished its first data load and rendered rows.
        if (_pendingScrollRestore is { } pending && !IsLoading && _scrollModule is not null)
        {
            _pendingScrollRestore = null;
            await _scrollModule.InvokeVoidAsync("setScrollPosition", pending);
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    /// <summary>
    /// Invoked from JS by the debounced scroll listener whenever the user scrolls.
    /// Updates only the scroll position in the state service, preserving page/pageSize/filters.
    /// </summary>
    [JSInvokable]
    public void OnScrollPositionChanged(double scrollTop) =>
        ListPageStateService.UpdateScrollPosition(GetRoutePath(), scrollTop);

    /// <summary>
    /// Re-restores the grid's <c>CurrentPage</c> from <see cref="_savedPage"/> after the
    /// <c>RowsPerPage</c> parameter setter has fired and clobbered it to 0 as a side effect.
    /// Setting <c>CurrentPage</c> from outside the component is normally flagged by the Blazor
    /// analyzer (BL0005), but the setter is well-behaved (updates the field, fires the change
    /// callback, and triggers a re-fetch) and this is the only mechanism MudDataGrid v9 exposes
    /// for programmatically navigating to an arbitrary page.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "BL0005:Component parameter should not be set outside of its component", Justification = "MudDataGrid v9 exposes no public method to set CurrentPage to an arbitrary index; the property setter is the documented mechanism and is well-behaved.")]
    private void RestoreCurrentPageAfterRowsPerPageReset()
    {
        if (_savedPage > 0 && GridRef is { } grid && grid.CurrentPage != _savedPage)
        {
            grid.CurrentPage = _savedPage;
        }
    }

    /// <summary>
    /// Extracts filters and sort from <paramref name="state"/>, manages the <see cref="CancellationTokenSource"/>,
    /// calls <paramref name="fetchAsync"/> with the extracted parameters, and handles errors uniformly.
    /// </summary>
    /// <param name="state">Grid state provided by MudDataGrid.</param>
    /// <param name="fetchAsync">
    /// Delegate that performs the actual data fetch. Receives: filters, pageNumber, pageSize,
    /// sortColumn, sortDirection, cancellationToken.
    /// </param>
    /// <param name="additionalFilters">
    /// Optional action to inject extra filters (e.g., search string, status dropdown) before the fetch.
    /// </param>
    /// <param name="showCancelSnackbar">Whether to show a snackbar on cancellation (organizer pages do, public pages don't).</param>
    protected async Task<GridData<TDto>> LoadServerDataAsync(
        GridState<TDto> state,
        Func<Dictionary<string, (string Operator, string Value)>, int, int, string?, string?, CancellationToken, Task<(IReadOnlyList<TDto> Items, int TotalItems)>> fetchAsync,
        Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null,
        bool showCancelSnackbar = true)
    {
        await ResetCancellationTokenAsync();

        IsLoading = true;
        StateHasChanged();

        var filters = ExtractGridFilters(state);
        additionalFilters?.Invoke(filters);

        var (sortColumn, sortDirection) = ExtractSortParameters(state);

        try
        {
            var (items, totalItems) = await fetchAsync(filters, state.Page + 1, state.PageSize, sortColumn, sortDirection, _cts!.Token);
            SaveCurrentState(state.Page, state.PageSize);
            return new GridData<TDto> { Items = items, TotalItems = totalItems };
        }
        catch (OperationCanceledException)
        {
            if (showCancelSnackbar)
                Snackbar.Add("Loading cancelled.", Severity.Info);
            return new GridData<TDto> { Items = [], TotalItems = 0 };
        }
        catch (Exception ex)
        {
            Snackbar.Add(ErrorMessages.LoadError(Title, ex), Severity.Error);
            return new GridData<TDto> { Items = [], TotalItems = 0 };
        }
        finally
        {
            IsLoading = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Loads a page of data for the mobile card view. Manages the CTS, loading state,
    /// and error handling identically to <see cref="LoadServerDataAsync"/>.
    /// </summary>
    protected async Task LoadMobileDataAsync(
        Func<Dictionary<string, (string Operator, string Value)>, int, int, string?, string?, CancellationToken, Task<(IReadOnlyList<TDto> Items, int TotalItems)>> fetchAsync,
        Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null)
    {
        await ResetCancellationTokenAsync();

        IsLoading = true;
        StateHasChanged();

        var filters = new Dictionary<string, (string Operator, string Value)>();
        additionalFilters?.Invoke(filters);

        try
        {
            var (items, totalItems) = await fetchAsync(filters, MobileCurrentPage, MobilePageSize, null, null, _cts!.Token);
            MobileItems = items;
            MobileTotalItems = totalItems;
            SaveCurrentState(0, MobilePageSize);
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or user cancellation
        }
        catch (Exception ex)
        {
            Snackbar.Add(ErrorMessages.LoadError(Title, ex), Severity.Error);
            MobileItems = [];
            MobileTotalItems = 0;
        }
        finally
        {
            IsLoading = false;
            StateHasChanged();
        }
    }

    private async Task ResetCancellationTokenAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _cts = new CancellationTokenSource();
    }

    private static Dictionary<string, (string Operator, string Value)> ExtractGridFilters(GridState<TDto> state) =>
        state.FilterDefinitions?
            .Where(f => !string.IsNullOrWhiteSpace(f.Column?.PropertyName))
            .ToDictionary(
                f => f.Column!.PropertyName!,
                f => (f.Operator ?? string.Empty, f.Value?.ToString() ?? string.Empty)
            ) ?? [];

    private static (string? SortColumn, string? SortDirection) ExtractSortParameters(GridState<TDto> state)
    {
        var sort = state.SortDefinitions?.FirstOrDefault();
        return (sort?.SortBy, sort?.Descending == true ? "desc" : "asc");
    }

    private void SaveCurrentState(int page, int pageSize)
    {
        var routePath = GetRoutePath();
        var existing = ListPageStateService.GetState(routePath);
        var filters = new Dictionary<string, string>();
        SaveFilters(filters);
        ListPageStateService.SaveState(routePath, new ListPageState
        {
            Page = page,
            PageSize = pageSize,
            MobilePage = MobileCurrentPage,
            Filters = filters,
            ScrollPosition = existing?.ScrollPosition ?? 0,
        });
    }

    private string GetRoutePath() => new Uri(NavigationManager.Uri).AbsolutePath;

    /// <summary>
    /// Called when the viewport switches to mobile or on first render in mobile mode.
    /// Override in derived pages to trigger <see cref="LoadMobileDataAsync"/>.
    /// </summary>
    protected virtual Task OnMobileDataRequestedAsync() => Task.CompletedTask;

    public void CancelLoading() => _cts?.Cancel();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_scrollModule is not null)
            {
                await _scrollModule.InvokeVoidAsync("disableScrollTracking", _scrollTrackerId);
                await _scrollModule.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit already torn down — nothing to clean up.
        }
        catch (JSException)
        {
            // Best-effort: ignore shutdown-time JS interop races.
        }
        finally
        {
            _dotNetRef?.Dispose();
        }

        try
        {
            await BrowserViewportService.UnsubscribeAsync(this);
        }
        catch
        {
            // Best-effort: JS interop may fail during app shutdown
        }

        await (_cts?.CancelAsync() ?? Task.CompletedTask);
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
