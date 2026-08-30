using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Resources;
using MudBlazor;

namespace MMCA.Common.UI.Components;

/// <summary>
/// Code-behind for the mobile infinite-scroll card list: page fetching via an IntersectionObserver
/// sentinel, a rendered-item cap bounding DOM growth, generation-guarded supersession of in-flight
/// fetches (<see cref="ResetAsync"/> bumps a generation counter AND cancels the outstanding token;
/// a fetch that completes under a superseded generation discards its results and leaves the page
/// counter alone), and localized load-failure handling with retry.
/// </summary>
/// <typeparam name="TItem">The list item type rendered by the card template.</typeparam>
public partial class MobileInfiniteScrollList<TItem> : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IToastService Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    [Parameter]
    [EditorRequired]
    public RenderFragment<TItem> CardTemplate { get; set; } = default!;

    /// <summary>
    /// The page fetcher, in the shape every Result-returning UI service already has
    /// (<c>IEntityService.GetPagedAsync</c> without the filter/sort arguments): page number
    /// (1-based), page size, cancellation token. Required. A failure <see cref="Result"/> renders
    /// the inline load-failure message plus its Retry button.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public Func<int, int, CancellationToken, Task<Result<(IReadOnlyList<TItem> Items, int TotalItems)>>>? FetchPageResult { get; set; }

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

    /// <summary>
    /// Incremented by <see cref="ResetAsync"/> to supersede every fetch already in flight. A load
    /// snapshots it before awaiting and discards its results when the value moved while it waited.
    /// </summary>
    private int _generation;

    private bool _isInitialLoad = true;
    private bool _isLoadingMore;
    private bool _hasMore = true;
    private bool _loadError;

    /// <summary>
    /// The localized message from a failure <see cref="Result"/>, rendered in place of the generic
    /// load-failure text. Null when the failure carried no message or came from an exception, in
    /// which case the generic resource string is shown instead.
    /// </summary>
    private string? _loadErrorMessage;

    private ElementReference _sentinelRef;
    private IJSObjectReference? _jsModule;
    private DotNetObjectReference<MobileInfiniteScrollList<TItem>>? _dotNetRef;
    private readonly string _observerId = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _cts;
    private bool _observerAttached;
    private bool _disposed;

    protected override async Task OnInitializedAsync()
    {
        ValidateFetchParameter();

        await LoadNextPageAsync(isInitial: true);
        _isInitialLoad = false;
    }

    /// <summary>
    /// <see cref="FetchPageResult"/> must be supplied. Checked here rather than inside the load, so
    /// a misconfigured call site fails loudly instead of rendering as a load failure with a Retry
    /// button that can never succeed.
    /// </summary>
    private void ValidateFetchParameter()
    {
        if (FetchPageResult is null)
        {
            throw new InvalidOperationException(
                "MobileInfiniteScrollList requires a FetchPageResult delegate.");
        }
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
        _loadErrorMessage = null;

        // Snapshot the generation this load belongs to. Whoever supersedes it (ResetAsync) owns
        // cancelling and disposing the token source it publishes here.
        int generation = _generation;
        var cts = new CancellationTokenSource();
        _cts = cts;

        // The page number is computed, not committed: _currentPage only advances on a successful,
        // non-superseded completion. A cancelled, failed, or superseded fetch therefore leaves the
        // counter untouched, so nothing has to be compensated back and no page is ever re-requested.
        int targetPage = _currentPage + 1;

        try
        {
            var fetched = await FetchPageResult!(targetPage, PageSize, cts.Token);

            // The generation, not the token, is authoritative: the fetcher is consumer-supplied and
            // may ignore the CancellationToken entirely, so a superseded fetch can still complete
            // successfully. This check is what keeps its rows out of the list ResetAsync just cleared.
            if (_disposed || generation != _generation)
            {
                return;
            }

            if (!fetched.TryGetValue(out var page))
            {
                // A failure Result raises the error state and its inline Retry button; the page
                // counter never advanced, so there is nothing to compensate back.
                SetLoadFailed(isInitial, fetched);
                return;
            }

            var (items, totalCount) = page;

            _currentPage = targetPage;
            _items.AddRange(items);
            _totalCount = totalCount;

            // Stop fetching once the rendered-item cap is reached so the DOM (and memory) stay
            // bounded even for very large result sets.
            _hasMore = _items.Count < _totalCount && _items.Count < MaxRenderedItems;
        }
        catch (OperationCanceledException)
        {
            // Superseded or disposed; the page counter never advanced, so there is nothing to undo.
        }
        catch (Exception)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            SetLoadFailed(isInitial, failure: null);
        }
        finally
        {
            if (generation == _generation)
            {
                // A superseding reset already cleared the flag (and the reload it started may have set
                // it again), so only the current generation is allowed to clear it.
                _isLoadingMore = false;
            }

            if (ReferenceEquals(_cts, cts))
            {
                // Only the still-current source is ours to dispose: a resetter that superseded this
                // load already cancelled and disposed the one it took over.
                _cts = null;
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Raises the error state both failure signals share: the inline message plus its Retry button,
    /// and on the very first load a snackbar as well (the initial failure renders as an empty state,
    /// so the snackbar is the only thing the user would otherwise see).
    /// </summary>
    /// <param name="isInitial">Whether this was the initial load.</param>
    /// <param name="failure">
    /// The failed result whose localized message is shown, or <see langword="null"/> for an
    /// exception, whose raw text is neither translatable nor safe to surface (ADR-027 / rubric §24)
    /// and which therefore falls back to the generic resource string.
    /// </param>
    private void SetLoadFailed(bool isInitial, Result? failure)
    {
        _loadError = true;
        _loadErrorMessage = failure?.LocalizedErrorMessage(L);

        if (isInitial)
        {
            Toast.Error(_loadErrorMessage ?? L["Grid.Snackbar.LoadFailed"].Value);
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
        // Supersede first: any fetch already awaiting FetchPageResult now belongs to an older generation
        // and must drop its results instead of appending them to the list cleared below.
        _generation++;

        var stale = _cts;
        _cts = null;

        if (stale is not null)
        {
            await stale.CancelAsync();
            stale.Dispose();
        }

        // The superseded load no longer owns the in-flight marker, and it will not clear it (its
        // generation moved). Without clearing it here the reload below would early-return, leaving
        // the list empty until the next sentinel hit.
        _isLoadingMore = false;

        _items.Clear();
        _currentPage = 0;
        _totalCount = 0;
        _hasMore = true;
        _loadError = false;
        _loadErrorMessage = null;
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
