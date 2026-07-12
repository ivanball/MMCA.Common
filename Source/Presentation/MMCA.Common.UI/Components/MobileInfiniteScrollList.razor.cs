using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using MMCA.Common.UI.Resources;
using MudBlazor;

namespace MMCA.Common.UI.Components;

/// <summary>
/// Code-behind for the mobile infinite-scroll card list: page fetching via an IntersectionObserver
/// sentinel, a rendered-item cap bounding DOM growth, cancellation of superseded fetches, and
/// localized load-failure handling with retry.
/// </summary>
/// <typeparam name="TItem">The list item type rendered by the card template.</typeparam>
public partial class MobileInfiniteScrollList<TItem> : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    [Parameter]
    [EditorRequired]
    public RenderFragment<TItem> CardTemplate { get; set; } = default!;

    [Parameter]
    [EditorRequired]
    public Func<int, int, CancellationToken, Task<(IReadOnlyList<TItem> Items, int TotalItems)>> FetchPage { get; set; } = default!;
    [Parameter] public int PageSize { get; set; } = 10;
    [Parameter] public EventCallback<TItem> OnCardClick { get; set; }

    /// <summary>Empty-state text; when null, <see cref="EmptyState"/> shows its localized default (ADR-027).</summary>
    [Parameter] public string? EmptyMessage { get; set; }

    [Parameter] public string EmptyIcon { get; set; } = Icons.Material.Outlined.SearchOff;

    /// <summary>
    /// Upper bound on how many items are loaded into the DOM. Infinite scroll stops fetching once
    /// the list reaches this many items, bounding DOM growth (and memory) for very large result
    /// sets. Raise it for consumers that genuinely need a longer list. Defaults to <c>500</c>.
    /// </summary>
    [Parameter] public int MaxRenderedItems { get; set; } = 500;

    private readonly List<TItem> _items = [];
    private int _totalCount;
    private int _currentPage;
    private bool _isInitialLoad = true;
    private bool _isLoadingMore;
    private bool _hasMore = true;
    private bool _loadError;
    private ElementReference _sentinelRef;
    private IJSObjectReference? _jsModule;
    private DotNetObjectReference<MobileInfiniteScrollList<TItem>>? _dotNetRef;
    private readonly string _observerId = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _cts;
    private bool _observerAttached;
    private bool _disposed;

    protected override async Task OnInitializedAsync()
    {
        await LoadNextPageAsync(isInitial: true);
        _isInitialLoad = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_disposed && _hasMore && !_observerAttached && _items.Count > 0 && !_isInitialLoad)
        {
            await AttachObserverAsync();
        }
    }

    private async Task AttachObserverAsync()
    {
        try
        {
            _jsModule ??= await JS.InvokeAsync<IJSObjectReference>(
                "import", "./_content/MMCA.Common.UI/infinite-scroll.js");
            _dotNetRef ??= DotNetObjectReference.Create(this);
            await _jsModule.InvokeVoidAsync("observe", _dotNetRef, _sentinelRef, _observerId);
            _observerAttached = true;
        }
        catch (JSDisconnectedException)
        {
            // Expected during app shutdown or prerendering
        }
    }

    private async Task DetachObserverAsync()
    {
        if (_jsModule is not null && _observerAttached)
        {
            try
            {
                await _jsModule.InvokeVoidAsync("unobserve", _observerId);
            }
            catch (JSDisconnectedException)
            {
                // Expected during app shutdown
            }

            _observerAttached = false;
        }
    }

    [JSInvokable]
    public async Task OnSentinelVisible()
    {
        if (_isLoadingMore || !_hasMore || _disposed)
        {
            return;
        }

        await InvokeAsync(async () =>
        {
            await LoadNextPageAsync(isInitial: false);
            StateHasChanged();
        });
    }

    private async Task LoadNextPageAsync(bool isInitial)
    {
        if (_isLoadingMore)
        {
            return;
        }

        _isLoadingMore = true;
        _loadError = false;

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _cts = new CancellationTokenSource();

        try
        {
            _currentPage++;
            var (items, totalCount) = await FetchPage(_currentPage, PageSize, _cts.Token);
            _items.AddRange(items);
            _totalCount = totalCount;

            // Stop fetching once the rendered-item cap is reached so the DOM (and memory) stay
            // bounded even for very large result sets.
            _hasMore = _items.Count < _totalCount && _items.Count < MaxRenderedItems;
        }
        catch (OperationCanceledException)
        {
            _currentPage--;
        }
        catch (Exception)
        {
            _currentPage--;
            _loadError = true;

            if (isInitial)
            {
                // Localized, sanitized message: raw exception text is neither translatable nor safe to
                // surface (ADR-027 / rubric §24).
                Snackbar.Add(L["Grid.Snackbar.LoadFailed"], Severity.Error);
            }
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    private async Task RetryAsync()
    {
        await LoadNextPageAsync(isInitial: false);
        StateHasChanged();
    }

    /// <summary>
    /// Clears all loaded items and reloads from page 1.
    /// Call this when search/filter criteria change.
    /// </summary>
    public async Task ResetAsync()
    {
        _items.Clear();
        _currentPage = 0;
        _totalCount = 0;
        _hasMore = true;
        _loadError = false;
        _isInitialLoad = true;

        await DetachObserverAsync();
        _observerAttached = false;

        StateHasChanged();

        await LoadNextPageAsync(isInitial: true);
        _isInitialLoad = false;
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        await DetachObserverAsync();

        if (_jsModule is not null)
        {
            try
            {
                await _jsModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Expected during app shutdown
            }
        }

        _dotNetRef?.Dispose();
    }
}
