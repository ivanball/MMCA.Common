using Microsoft.AspNetCore.Components;
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

    protected bool IsLoading { get; private set; }
    protected abstract string Title { get; }

    /// <summary>True when the viewport breakpoint is <see cref="Breakpoint.Xs"/> (phone-sized, ≤ 600 px).</summary>
    protected bool IsMobile { get; private set; }

    // ── Mobile card-view state ──
    protected IReadOnlyList<TDto> MobileItems { get; private set; } = [];
    protected int MobileTotalItems { get; private set; }
    protected int MobileCurrentPage { get; set; } = 1;
    protected int MobilePageSize { get; set; } = 10;

    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <inheritdoc />
    public Guid Id { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public ResizeOptions ResizeOptions { get; } = new() { ReportRate = 250 };

    /// <inheritdoc />
    public async Task NotifyBrowserViewportChangeAsync(BrowserViewportEventArgs browserViewportEventArgs) =>
        await InvokeAsync(async () =>
        {
            var wasMobile = IsMobile;
            IsMobile = browserViewportEventArgs.Breakpoint == Breakpoint.Xs;

            if (IsMobile && !wasMobile)
            {
                MobileCurrentPage = 1;
                await OnMobileDataRequestedAsync();
            }

            StateHasChanged();
        });

    /// <summary>Subscribes to viewport changes after the first render (JS interop requires a rendered DOM).</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await BrowserViewportService.SubscribeAsync(this, fireImmediately: true);
        }

        await base.OnAfterRenderAsync(firstRender);
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
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _cts = new CancellationTokenSource();

        IsLoading = true;
        StateHasChanged();

        var filters = state.FilterDefinitions?
            .Where(f => !string.IsNullOrWhiteSpace(f.Column?.PropertyName))
            .ToDictionary(
                f => f.Column!.PropertyName!,
                f => (f.Operator ?? string.Empty, f.Value?.ToString() ?? string.Empty)
            ) ?? [];

        additionalFilters?.Invoke(filters);

        int pageNumber = state.Page + 1;
        int pageSize = state.PageSize;
        var sort = state.SortDefinitions?.FirstOrDefault();
        string? sortColumn = sort?.SortBy;
        string? sortDirection = sort?.Descending == true ? "desc" : "asc";

        try
        {
            var (items, totalItems) = await fetchAsync(filters, pageNumber, pageSize, sortColumn, sortDirection, _cts.Token);
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
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _cts = new CancellationTokenSource();

        IsLoading = true;
        StateHasChanged();

        var filters = new Dictionary<string, (string Operator, string Value)>();
        additionalFilters?.Invoke(filters);

        try
        {
            var (items, totalItems) = await fetchAsync(filters, MobileCurrentPage, MobilePageSize, null, null, _cts.Token);
            MobileItems = items;
            MobileTotalItems = totalItems;
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
