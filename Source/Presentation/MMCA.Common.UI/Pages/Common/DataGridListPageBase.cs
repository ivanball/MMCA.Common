using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Resources;
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
    [Inject] protected IToastService Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedResource> Localizer { get; set; } = default!;
    [Inject] private IBrowserViewportService BrowserViewportService { get; set; } = default!;
    [Inject] private ListPageStateService ListPageStateService { get; set; } = default!;
    [Inject] private ListPageQueryStateService QueryStateService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private PersistentComponentState ApplicationState { get; set; } = default!;

    protected bool IsLoading { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the most recent grid/mobile fetch failed. On failure the
    /// grid renders with zero rows, which is visually identical to a genuinely empty list once
    /// the error toast expires, so pages should branch on this flag in
    /// <c>NoRecordsContent</c> (or above the mobile list) to show an inline
    /// error-with-retry instead of the "no records" empty state. Reset by the next fetch.
    /// </summary>
    protected bool LoadFailed { get; private set; }
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

    /// <summary>
    /// Compact (dense) grid density toggle. Bind in Razor via <c>Dense="@DenseGrid"</c> on the
    /// <see cref="MudDataGrid{T}"/> and surface a toggle that calls <see cref="ToggleDensity"/>.
    /// Restored from saved state on initialization and persisted (URL + in-memory + sessionStorage)
    /// so the user's density choice survives navigation, refresh, and shareable links. Defaults to
    /// <see langword="false"/> (comfortable density).
    /// </summary>
    protected bool DenseGrid { get; private set; }

    // Upper bound (ms) on the SSR pre-render data fetch. In prod the backend is warm and the fetch
    // completes well under this, so the persist/restore optimization below still works; under a cold
    // or unreachable backend (e.g. CI cold-start) it caps how long prerender can block before falling
    // back to an empty grid that the first interactive ServerData call refills.
    private const int PrerenderFetchTimeoutMs = 5000;

    // MudDataGrid v9 puts its height-bound scroll viewport on `.mud-table-container`. A virtualized
    // grid scrolls THERE, not on the document, so scroll tracking and restore have to follow it.
    private const string VirtualizedScrollContainerSelector = ".mud-table-container";

    private CancellationTokenSource? _cts;
    private bool _disposed;
    private IJSObjectReference? _scrollModule;
    private PersistingComponentStateSubscription? _persistenceSubscription;
    private GridData<TDto>? _persistedGridData;
    private GridData<TDto>? _lastSuccessfulGridData;
    private DotNetObjectReference<DataGridListPageBase<TDto>>? _dotNetRef;
    private double? _pendingScrollRestore;
    private int _savedPage;
    private int _savedPageSize;
    private string? _savedSortColumn;
    private bool _savedSortDescending;
    private bool _suppressNextLocationChanged;
    private bool _locationHandlerRegistered;
    private bool _deferSessionPersist;
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

    /// <summary>
    /// Opt-in switch for MudDataGrid row virtualization. Defaults to <see langword="false"/>, so every
    /// existing page keeps its pager and its <c>ServerData</c> binding untouched. A page that overrides
    /// this to <see langword="true"/> binds <c>Virtualize="true"</c>, <c>Height="@VirtualizedGridHeight"</c>,
    /// <c>ItemSize="VirtualizedItemSize"</c> and <c>VirtualizeServerData</c> (wired to
    /// <see cref="LoadVirtualizedServerDataAsync"/>) in its markup INSTEAD of <c>ServerData</c>: MudBlazor
    /// v9 accepts only one of the two funnels, and binding both leaves the grid fetching through the
    /// pager it no longer renders. Turning this on also disables the pager-restore machinery
    /// (rows-per-page / current-page restoration and the <c>p</c>/<c>ps</c> URL mirror), which has no
    /// meaning without a pager; sort, filter and density persistence still apply.
    /// </summary>
    protected virtual bool VirtualizeGrid => false;

    /// <summary>
    /// The CSS height bound to <c>Height</c> on a virtualized grid. MudDataGrid v9 virtualizes only when
    /// <c>Height</c> is set (the scroll viewport is what bounds the rendered window), so this is
    /// mandatory rather than cosmetic. Defaults to <c>70vh</c>, which leaves room for the page header and
    /// toolbar on a laptop viewport; override for a page whose chrome is taller or shorter.
    /// </summary>
    protected virtual string VirtualizedGridHeight => "70vh";

    /// <summary>
    /// The row height in pixels bound to <c>ItemSize</c> on a virtualized grid. MudBlazor sizes the
    /// spacer elements above and below the rendered window from this number, so a value far from the
    /// real rendered row height makes the scrollbar drift and the fetch window overshoot. Defaults to
    /// <c>52</c>, the comfortable-density MudDataGrid row height; a dense grid is nearer <c>36</c>.
    /// </summary>
    protected virtual int VirtualizedItemSize => 52;

    /// <summary>
    /// Reads URL query string as the source of truth for paging, sort, and filter state,
    /// then merges in the in-memory <see cref="ListPageStateService"/> entry for scroll
    /// position (which is too noisy to keep in the URL). Subscribes to
    /// <see cref="NavigationManager.LocationChanged"/> so browser back/forward navigation
    /// re-applies state and reloads the grid.
    /// </summary>
    protected override void OnInitialized()
    {
        // Restore grid data persisted during SSR pre-render so the first interactive
        // ServerData call returns immediately without a redundant API round-trip.
        // This eliminates the visible cancel-retry cycle caused by the InteractiveAuto
        // render mode transition (SSR → Server → WASM) and MudDataGrid re-initialization.
        var persistKey = $"grid:{GetType().FullName}";
        if (ApplicationState.TryTakeFromJson<PersistedGridState>(persistKey, out var restored) && restored is not null)
        {
            _persistedGridData = new GridData<TDto> { Items = restored.Items, TotalItems = restored.TotalItems };
        }

        // An explicit render mode is required: this page inherits its render mode from
        // <Routes @rendermode="InteractiveAuto"> rather than declaring one itself, so the
        // framework cannot infer a render mode for the persistence callback during the static
        // prerender pass (InferRenderModes) and throws
        // "The registered callback <OnInitialized> must be associated with a component or
        // define an explicit render mode". Passing InteractiveAuto resolves the association
        // while keeping the SSR-prerender persist/restore optimization intact.
        _persistenceSubscription = ApplicationState.RegisterOnPersisting(
            () =>
            {
                if (_lastSuccessfulGridData is not null)
                {
                    ApplicationState.PersistAsJson(persistKey, new PersistedGridState([.. _lastSuccessfulGridData.Items], _lastSuccessfulGridData.TotalItems));
                }

                return Task.CompletedTask;
            },
            Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveAuto);

        // Pin THIS page's route while it is provably current. Grid-state writes are inherently
        // deferred (debounced search, late ServerData completions), so deriving the route from
        // NavigationManager.Uri at WRITE time raced page navigation: a stale write saved state
        // under the NEXT page's key and stamped grid params onto its URL (E2E-diagnosed spurious
        // navigation to /inventory/create?ps=10 that disposed freshly-navigated detail pages).
        _ownRoutePath = new Uri(NavigationManager.Uri).AbsolutePath;

        var urlState = QueryStateService.ReadCurrent();
        var routePath = GetRoutePath();
        var savedState = ListPageStateService.GetState(routePath);

        var urlHasState = HasListPageState(urlState);

        // When the URL carries state (browser back/forward, shareable link), use it.
        // Otherwise, fall back to in-memory state from the current circuit — this
        // restores page, pageSize, sort, and filters when the user navigates back
        // to the list via sidebar, breadcrumbs, or "Back to List" buttons instead
        // of the browser back button.
        var effectiveState = urlHasState || savedState is null ? urlState : savedState;

        CurrentPageState = effectiveState.Page;
        _savedPage = effectiveState.Page;
        _savedPageSize = effectiveState.PageSize;
        if (effectiveState.PageSize > 0)
        {
            RowsPerPageState = effectiveState.PageSize;
        }

        MobileCurrentPage = effectiveState.MobilePage;
        _savedSortColumn = effectiveState.SortColumn;
        _savedSortDescending = effectiveState.SortDescending;
        DenseGrid = effectiveState.DenseGrid;
        RestoreFilters(effectiveState.Filters);

        // When neither URL nor in-memory state is available (new circuit after
        // forceLoad or session teardown), defer the sessionStorage write so that
        // OnAfterRenderAsync can hydrate the original values before they are
        // overwritten by the first LoadServerDataAsync call.
        _deferSessionPersist = !urlHasState && savedState is null;

        // Scroll position is not in the URL — fall back to the in-memory snapshot.
        if (savedState is { ScrollPosition: > 0 })
        {
            _pendingScrollRestore = savedState.ScrollPosition;
        }

        NavigationManager.LocationChanged += OnLocationChanged;
        _locationHandlerRegistered = true;

        base.OnInitialized();
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_suppressNextLocationChanged)
        {
            _suppressNextLocationChanged = false;
            return;
        }

        // Only react when the user navigates within the same list page (back/forward
        // between filtered states). Different paths are handled by component disposal.
        var newPath = new Uri(e.Location, UriKind.Absolute).AbsolutePath;
        if (!string.Equals(newPath, GetRoutePath(), StringComparison.Ordinal))
        {
            return;
        }

        var urlState = QueryStateService.ReadCurrent();
        _savedPage = urlState.Page;
        _savedPageSize = urlState.PageSize;
        if (urlState.PageSize > 0)
        {
            RowsPerPageState = urlState.PageSize;
        }
        _savedSortColumn = urlState.SortColumn;
        _savedSortDescending = urlState.SortDescending;
        DenseGrid = urlState.DenseGrid;
        MobileCurrentPage = urlState.MobilePage;
        RestoreFilters(urlState.Filters);

        _ = InvokeAsync(async () =>
        {
            if (GridRef is { } grid)
            {
                // A virtualized grid renders no pager, so there is no CurrentPage to re-apply; the
                // reload alone re-reads the restored sort/filters.
                if (!VirtualizeGrid)
                {
                    ApplyCurrentPageFromUrl(grid, urlState.Page);
                }

                await grid.ReloadServerData();
            }
            StateHasChanged();
        });
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "BL0005:Component parameter should not be set outside of its component", Justification = "MudDataGrid v9 exposes no public method to set CurrentPage to an arbitrary index; the property setter is the documented mechanism and is well-behaved.")]
    private static void ApplyCurrentPageFromUrl(MudDataGrid<TDto> grid, int targetPage)
    {
        if (grid.CurrentPage != targetPage)
        {
            grid.CurrentPage = targetPage;
        }
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
            // Hydrate scroll/state from sessionStorage now that JS interop is available
            // (the SSR-time OnInitialized only saw the in-memory dictionary, which is
            // empty after a circuit teardown or forceLoad navigation).
            var routePath = GetRoutePath();
            await ListPageStateService.HydrateFromSessionAsync(routePath);

            // Cross-circuit fallback: when neither URL nor in-memory state was available
            // during OnInitialized (new circuit after forceLoad or session teardown),
            // check if sessionStorage hydration recovered any list-page state.
            var hydrated = ListPageStateService.GetState(routePath);
            var needsSessionRestore = _deferSessionPersist && hydrated is not null && HasListPageState(hydrated);

            if (needsSessionRestore)
            {
                ApplyRestoredState(hydrated!);
            }

            if (_pendingScrollRestore is null && hydrated is { ScrollPosition: > 0 })
            {
                _pendingScrollRestore = hydrated.ScrollPosition;
            }

            _deferSessionPersist = false;

            await BrowserViewportService.SubscribeAsync(this, fireImmediately: true);

            _scrollModule = await JS.InvokeAsync<IJSObjectReference>(
                "import",
                "./_content/MMCA.Common.UI/list-page-scroll.js");
            _dotNetRef = DotNetObjectReference.Create(this);
            await _scrollModule.InvokeVoidAsync(
                "enableScrollTracking",
                _dotNetRef,
                _scrollTrackerId,
                150,
                ScrollContainerSelector);

            await RestoreGridStateAsync(needsSessionRestore);

            // Ensure the current state is persisted to sessionStorage. This covers the
            // deferred case (first load skipped persist) and keeps sessionStorage in sync
            // after hydration/restoration.
            _ = ListPageStateService.PersistToSessionAsync(routePath).AsTask();
        }

        // Restore scroll only after the grid has finished its first data load and rendered rows.
        if (_pendingScrollRestore is { } pending && !IsLoading && _scrollModule is not null)
        {
            _pendingScrollRestore = null;
            await _scrollModule.InvokeVoidAsync("setScrollPosition", pending, ScrollContainerSelector);
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    /// <summary>
    /// The element whose <c>scrollTop</c> is tracked and restored: the grid's own scroll viewport when
    /// virtualization is on (the document itself does not scroll then, because the grid is height-bound),
    /// otherwise <see langword="null"/> for the document scroller.
    /// </summary>
    private string? ScrollContainerSelector => VirtualizeGrid ? VirtualizedScrollContainerSelector : null;

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

    private static bool HasListPageState(ListPageState state) =>
        state.Page > 0 || state.PageSize > 0 || state.MobilePage > 1
        || !string.IsNullOrEmpty(state.SortColumn) || state.Filters.Count > 0;

    private void ApplyRestoredState(ListPageState state)
    {
        _savedPage = state.Page;
        _savedPageSize = state.PageSize;
        CurrentPageState = state.Page;
        if (state.PageSize > 0)
        {
            RowsPerPageState = state.PageSize;
        }

        MobileCurrentPage = state.MobilePage;
        _savedSortColumn = state.SortColumn;
        _savedSortDescending = state.SortDescending;
        DenseGrid = state.DenseGrid;
        RestoreFilters(state.Filters);
    }

    /// <summary>
    /// Restores the grid's <c>RowsPerPage</c> and <c>CurrentPage</c> from saved state,
    /// then optionally triggers a reload when sessionStorage provided state that was not
    /// available during <c>OnInitialized</c>.
    /// </summary>
    private async Task RestoreGridStateAsync(bool needsReload)
    {
        // Single entry point for the pager-restore machinery, so opting into virtualization skips it
        // in ONE place instead of guarding each step: a virtualized grid renders no pager, so
        // RowsPerPage and CurrentPage carry no meaning there. A session-driven reload is still needed
        // (sort/filters were restored after the first fetch returned defaults).
        if (VirtualizeGrid)
        {
            if (needsReload && GridRef is { } virtualizedGrid)
            {
                await virtualizedGrid.ReloadServerData();
            }

            return;
        }

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

        // Session restore changed pagination state after the grid's initial ServerData
        // call already returned defaults — reload with the correct parameters.
        if (needsReload && GridRef is { } reloadGrid)
        {
            await reloadGrid.ReloadServerData();
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
    /// <remarks>
    /// The delegate's shape mirrors <c>IEntityService.GetPagedAsync</c> exactly, so a page still
    /// passes the method group. A failed <see cref="Result"/> is handled here the same way an
    /// exception used to be: the localized message goes to the toast, <see cref="LoadFailed"/>
    /// is set, and the grid renders zero rows.
    /// </remarks>
    protected async Task<GridData<TDto>> LoadServerDataAsync(
        GridState<TDto> state,
        Func<Dictionary<string, (string Operator, string Value)>, int, int, string?, string?, CancellationToken, Task<Result<(IReadOnlyList<TDto> Items, int TotalItems)>>> fetchAsync,
        Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null,
        bool showCancelSnackbar = true)
    {
        await ResetCancellationTokenAsync();

        // Return SSR-persisted data on the first interactive call, avoiding a redundant
        // API round-trip that would be immediately canceled by the MudDataGrid pager init.
        if (_persistedGridData is not null)
        {
            var cached = _persistedGridData;
            _persistedGridData = null;
            _lastSuccessfulGridData = cached;

            var (sc, sd) = ResolveSortParameters(state.SortDefinitions);
            SaveCurrentState(state.Page, state.PageSize, sc, string.Equals(sd, "desc", StringComparison.OrdinalIgnoreCase));
            return cached;
        }

        IsLoading = true;
        LoadFailed = false;
        StateHasChanged();

        // Bound the fetch token: during SSR pre-render (CreateFetchCts) it times out so a cold/unreachable
        // backend can't block prerendering — and therefore the page load / navigation — indefinitely (the
        // dominant cause of E2E navigation timeouts). On timeout the fetch throws OperationCanceledException
        // and we return an empty grid; the first INTERACTIVE ServerData call then loads the real data.
        // No extra token to link: the paged funnel has no per-request token of its own.
        using var fetchCts = CreateFetchCts(CancellationToken.None);

        // Filter and sort extraction run INSIDE the try: the caller's additionalFilters callback is
        // arbitrary page code, and a throw from it (or from the extraction itself) used to escape
        // past the finally, stranding IsLoading at true and leaving the grid spinning forever.
        try
        {
            var filters = ExtractGridFilters(state.FilterDefinitions);
            additionalFilters?.Invoke(filters);

            var (sortColumn, sortDirection) = ResolveSortParameters(state.SortDefinitions);

            var fetched = await fetchAsync(filters, state.Page + 1, state.PageSize, sortColumn, sortDirection, fetchCts.Token);
            if (!fetched.TryGetValue(out var page))
            {
                fetched.NotifyOnFailure(Toast, Localizer);
                LoadFailed = true;
                return new GridData<TDto> { Items = [], TotalItems = 0 };
            }

            var gridData = new GridData<TDto> { Items = page.Items, TotalItems = page.TotalItems };
            _lastSuccessfulGridData = gridData;
            SaveCurrentState(state.Page, state.PageSize, sortColumn, string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase));
            return gridData;
        }
        catch (OperationCanceledException)
        {
            // Covers user/disposal cancellation and the pre-render timeout. During pre-render the
            // toast is a no-op (separate render: no JS toast host), so no special-casing is needed.
            if (showCancelSnackbar)
                Toast.Info(Localizer["Grid.Snackbar.LoadCancelled"]);
            return new GridData<TDto> { Items = [], TotalItems = 0 };
        }
        catch (Exception ex)
        {
            // Still guarded: the caller's additionalFilters callback and the grid-state extraction
            // above are arbitrary page code, and a throw from either must not strand IsLoading.
            Toast.Error(ErrorMessages.LoadError(Title, ex));
            LoadFailed = true;
            return new GridData<TDto> { Items = [], TotalItems = 0 };
        }
        finally
        {
            IsLoading = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// The <c>VirtualizeServerData</c> counterpart of <see cref="LoadServerDataAsync"/>: it manages the
    /// <see cref="CancellationTokenSource"/>, loading/failure state and error toast identically, but maps
    /// the row window MudBlazor asks for (<c>[StartIndex, StartIndex + Count)</c>) onto the same
    /// page-based fetch delegate, so a page can switch to virtualization without a second API contract.
    /// </summary>
    /// <param name="state">Virtualized grid state provided by MudDataGrid.</param>
    /// <param name="fetchAsync">
    /// Delegate that performs the actual data fetch. Receives: filters, pageNumber, pageSize,
    /// sortColumn, sortDirection, cancellationToken. Called twice when the requested window straddles
    /// two pages (see <see cref="ComputeVirtualWindow"/>).
    /// </param>
    /// <param name="additionalFilters">
    /// Optional action to inject extra filters (e.g., search string, status dropdown) before the fetch.
    /// </param>
    /// <param name="cancellationToken">
    /// The token MudBlazor hands the <c>VirtualizeServerData</c> callback (it cancels a window that the
    /// user has already scrolled past). Forward it so a superseded fetch stops at the API boundary too;
    /// omitting it still works, because the base class supersedes its own previous fetch on every call.
    /// </param>
    /// <remarks>
    /// Cancellation is ALWAYS silent here, unlike the paged path: a virtualized grid supersedes its own
    /// in-flight fetch on every scroll burst, so a cancel toast would fire continuously during normal
    /// scrolling and say nothing the user needs to act on.
    /// </remarks>
    protected async Task<GridData<TDto>> LoadVirtualizedServerDataAsync(
        GridStateVirtualize<TDto> state,
        Func<Dictionary<string, (string Operator, string Value)>, int, int, string?, string?, CancellationToken, Task<Result<(IReadOnlyList<TDto> Items, int TotalItems)>>> fetchAsync,
        Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fetchAsync);

        await ResetCancellationTokenAsync();

        IsLoading = true;
        LoadFailed = false;
        StateHasChanged();

        using var fetchCts = CreateFetchCts(cancellationToken);

        try
        {
            var filters = ExtractGridFilters(state.FilterDefinitions);
            additionalFilters?.Invoke(filters);

            var (sortColumn, sortDirection) = ResolveSortParameters(state.SortDefinitions);
            var window = ComputeVirtualWindow(state.StartIndex, state.Count);

            var fetched = await fetchAsync(filters, window.FirstPage, window.PageSize, sortColumn, sortDirection, fetchCts.Token);
            if (!fetched.TryGetValue(out var page))
            {
                fetched.NotifyOnFailure(Toast, Localizer);
                LoadFailed = true;
                return new GridData<TDto> { Items = [], TotalItems = 0 };
            }

            var items = page.Items;
            if (window.NeedsSecondPage)
            {
                var continued = await fetchAsync(filters, window.FirstPage + 1, window.PageSize, sortColumn, sortDirection, fetchCts.Token);
                if (!continued.TryGetValue(out var nextPage))
                {
                    continued.NotifyOnFailure(Toast, Localizer);
                    LoadFailed = true;
                    return new GridData<TDto> { Items = [], TotalItems = 0 };
                }

                items = [.. items, .. nextPage.Items];
            }

            // Trim to exactly the requested window; the tail of the data set legitimately yields fewer.
            var slice = items.Skip(window.Offset).Take(Math.Max(state.Count, 0)).ToList();
            SaveCurrentState(0, 0, sortColumn, string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase));
            return new GridData<TDto> { Items = slice, TotalItems = page.TotalItems };
        }
        catch (OperationCanceledException)
        {
            // Deliberately silent — see the remarks above.
            return new GridData<TDto> { Items = [], TotalItems = 0 };
        }
        catch (Exception ex)
        {
            Toast.Error(ErrorMessages.LoadError(Title, ex));
            LoadFailed = true;
            return new GridData<TDto> { Items = [], TotalItems = 0 };
        }
        finally
        {
            IsLoading = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Maps a virtualization row window onto the page-based fetch contract. The window's own size
    /// becomes the page size, so an aligned window (<paramref name="startIndex"/> a multiple of
    /// <paramref name="count"/>) is exactly one page and needs one fetch; an unaligned window spills
    /// into the following page, which is fetched too and concatenated before the caller slices
    /// <c>Offset</c> rows off the front.
    /// </summary>
    /// <param name="startIndex">Zero-based index of the first requested row.</param>
    /// <param name="count">Number of requested rows. Zero or less is clamped to a page size of 1.</param>
    /// <returns>
    /// The 1-based first page to fetch, the page size to fetch it with, how many leading rows of that
    /// page fall before the window, and whether the following page is needed as well.
    /// </returns>
    internal static (int FirstPage, int PageSize, int Offset, bool NeedsSecondPage) ComputeVirtualWindow(int startIndex, int count)
    {
        var pageSize = Math.Max(count, 1);
        var start = Math.Max(startIndex, 0);
        var offset = start % pageSize;

        var firstPage = start / pageSize + 1;

        return (firstPage, pageSize, offset, offset > 0);
    }

    /// <summary>
    /// Builds the cancellation token source for a <c>ServerData</c> fetch. Always linked to the active
    /// request token; during SSR pre-render (non-interactive) it additionally times out after
    /// <see cref="PrerenderFetchTimeoutMs"/> so a cold or unreachable backend cannot block prerendering
    /// (and therefore the page load) indefinitely. The caller owns disposal via a <see langword="using"/> statement.
    /// </summary>
    /// <param name="additionalToken">
    /// An extra token to link in (the virtualized path forwards MudBlazor's own per-window token).
    /// <see cref="CancellationToken.None"/> links nothing extra.
    /// </param>
    private CancellationTokenSource CreateFetchCts(CancellationToken additionalToken)
    {
        var cts = additionalToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token, additionalToken)
            : CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);
        if (!RendererInfo.IsInteractive)
        {
            cts.CancelAfter(PrerenderFetchTimeoutMs);
        }

        return cts;
    }

    /// <summary>
    /// Loads a page of data for the mobile card view. Manages the CTS, loading state,
    /// and error handling identically to <see cref="LoadServerDataAsync"/>.
    /// </summary>
    protected async Task LoadMobileDataAsync(
        Func<Dictionary<string, (string Operator, string Value)>, int, int, string?, string?, CancellationToken, Task<Result<(IReadOnlyList<TDto> Items, int TotalItems)>>> fetchAsync,
        Action<Dictionary<string, (string Operator, string Value)>>? additionalFilters = null)
    {
        await ResetCancellationTokenAsync();

        IsLoading = true;
        LoadFailed = false;
        StateHasChanged();

        var filters = new Dictionary<string, (string Operator, string Value)>();
        additionalFilters?.Invoke(filters);

        try
        {
            var fetched = await fetchAsync(filters, MobileCurrentPage, MobilePageSize, null, null, _cts!.Token);
            if (!fetched.TryGetValue(out var page))
            {
                fetched.NotifyOnFailure(Toast, Localizer);
                LoadFailed = true;
                MobileItems = [];
                MobileTotalItems = 0;
                return;
            }

            MobileItems = page.Items;
            MobileTotalItems = page.TotalItems;
            SaveCurrentState(0, MobilePageSize, _savedSortColumn, _savedSortDescending);
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or user cancellation
        }
        catch (Exception ex)
        {
            Toast.Error(ErrorMessages.LoadError(Title, ex));
            LoadFailed = true;
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
        // Swap in a fresh source FIRST so the caller always has a valid (non-disposed) token, then
        // tear down the previous one. A debounced grid reload (e.g. a search-box blur) can fire
        // AFTER the component disposed its CTS; cancelling an already-disposed source throws
        // ObjectDisposedException, which would surface as an unhandled render exception and trip the
        // blazor-error-ui banner. Tolerate that race instead.
        var previous = _cts;
        _cts = new CancellationTokenSource();

        if (previous is not null)
        {
            try
            {
                await previous.CancelAsync();
                previous.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Component was disposed mid-flight; the previous source is already gone.
            }
        }
    }

    /// <summary>
    /// Flattens the grid's filter definitions into the one-filter-per-column shape the fetch
    /// delegate takes. MudDataGrid lets the user add several filter rows on the SAME column, which
    /// a dictionary cannot carry, so the newest row wins instead of the projection throwing.
    /// </summary>
    /// <remarks>
    /// Takes the definition collection rather than the state object so the paged
    /// (<see cref="GridState{T}"/>) and virtualized (<see cref="GridStateVirtualize{T}"/>) funnels
    /// share one implementation; MudBlazor v9 exposes the same definition types on both.
    /// </remarks>
    private static Dictionary<string, (string Operator, string Value)> ExtractGridFilters(
        IEnumerable<IFilterDefinition<TDto>>? filterDefinitions) =>
        filterDefinitions?
            .Where(f => !string.IsNullOrWhiteSpace(f.Column?.PropertyName))
            .GroupBy(f => f.Column!.PropertyName!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var newest = g.Last();
                    return (newest.Operator ?? string.Empty, newest.Value?.ToString() ?? string.Empty);
                },
                StringComparer.Ordinal
            ) ?? [];

    private static (string? SortColumn, string? SortDirection) ExtractSortParameters(
        IEnumerable<SortDefinition<TDto>>? sortDefinitions)
    {
        var sort = sortDefinitions?.FirstOrDefault();
        return (sort?.SortBy, sort?.Descending == true ? "desc" : "asc");
    }

    /// <summary>
    /// The grid's own sort, with a first-fetch fallback: when MudDataGrid has not yet picked up a
    /// SortDefinition (typical on initial load with a URL-driven sort), the sort restored from the
    /// query string is used instead, so the data lands sorted from the very first request.
    /// </summary>
    private (string? SortColumn, string? SortDirection) ResolveSortParameters(
        IEnumerable<SortDefinition<TDto>>? sortDefinitions)
    {
        var (sortColumn, sortDirection) = ExtractSortParameters(sortDefinitions);

        if (string.IsNullOrEmpty(sortColumn) && !string.IsNullOrEmpty(_savedSortColumn))
        {
            sortColumn = _savedSortColumn;
            sortDirection = _savedSortDescending ? "desc" : "asc";
        }

        return (sortColumn, sortDirection);
    }

    private void SaveCurrentState(int page, int pageSize, string? sortColumn, bool sortDescending)
    {
        // Drop stale writes outright: a debounced/late save landing after navigation must
        // neither persist under a foreign route key nor mirror grid params onto that page's URL.
        if (!IsOwnRouteCurrent())
        {
            return;
        }

        var routePath = GetRoutePath();
        var existing = ListPageStateService.GetState(routePath);
        var filters = new Dictionary<string, string>();
        SaveFilters(filters);
        var state = new ListPageState
        {
            Page = page,
            PageSize = pageSize,
            MobilePage = MobileCurrentPage,
            SortColumn = sortColumn,
            SortDescending = sortDescending,
            DenseGrid = DenseGrid,
            Filters = filters,
            ScrollPosition = existing?.ScrollPosition ?? 0,
        };
        ListPageStateService.SaveState(routePath, state);

        // Mirror to URL (replace current entry — filter changes must not pollute the back stack)
        // and to sessionStorage so the state survives circuit teardown / forceLoad navigations.
        _suppressNextLocationChanged = true;
        QueryStateService.ReplaceState(routePath, state);
        // Fire-and-forget the sessionStorage write — it tolerates SSR/JSDisconnected internally.
        // Skip during the deferred window so OnAfterRenderAsync can still hydrate the original
        // sessionStorage values before they are overwritten.
        if (!_deferSessionPersist)
        {
            _ = ListPageStateService.PersistToSessionAsync(routePath).AsTask();
        }
    }

    /// <summary>
    /// Flips the grid density (comfortable ↔ dense) and persists the choice (URL + in-memory +
    /// sessionStorage) so it survives navigation, refresh, and shareable links. Wire a toggle in the
    /// derived page's markup to call this; the bound <c>Dense="@DenseGrid"</c> re-renders the grid.
    /// </summary>
    protected void ToggleDensity()
    {
        DenseGrid = !DenseGrid;
        PersistDensity();
        StateHasChanged();
    }

    /// <summary>
    /// Writes the current <see cref="DenseGrid"/> value through to the saved <see cref="ListPageState"/>,
    /// the URL query string, and sessionStorage, preserving all other fields. Mirrors the persistence
    /// tail of <see cref="SaveCurrentState"/> but updates only the density, so a toggle made before the
    /// grid's first <c>ServerData</c> save is not lost.
    /// </summary>
    private void PersistDensity()
    {
        // Same stale-write guard as SaveCurrentState: never persist/mirror after navigating away.
        if (!IsOwnRouteCurrent())
        {
            return;
        }

        var routePath = GetRoutePath();
        var existing = ListPageStateService.GetState(routePath) ?? new ListPageState();
        var updated = existing with { DenseGrid = DenseGrid };
        ListPageStateService.SaveState(routePath, updated);

        _suppressNextLocationChanged = true;
        QueryStateService.ReplaceState(routePath, updated);

        if (!_deferSessionPersist)
        {
            _ = ListPageStateService.PersistToSessionAsync(routePath).AsTask();
        }
    }

    // The route pinned at OnInitialized; falls back to the live URI only before init.
    private string? _ownRoutePath;

    private string GetRoutePath() => _ownRoutePath ?? new Uri(NavigationManager.Uri).AbsolutePath;

    /// <summary>
    /// True while this list page's route is still the CURRENT location. Deferred grid-state
    /// writes must be dropped once the user navigated away (see <c>_ownRoutePath</c>).
    /// </summary>
    private bool IsOwnRouteCurrent() =>
        string.Equals(new Uri(NavigationManager.Uri).AbsolutePath, GetRoutePath(), StringComparison.OrdinalIgnoreCase);

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

        _persistenceSubscription?.Dispose();
        UnsubscribeLocationChanged();

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
            _persistenceSubscription?.Dispose();
            UnsubscribeLocationChanged();
            _cts?.Cancel();
            _cts?.Dispose();
        }

        _disposed = true;
    }

    private void UnsubscribeLocationChanged()
    {
        if (_locationHandlerRegistered)
        {
            NavigationManager.LocationChanged -= OnLocationChanged;
            _locationHandlerRegistered = false;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Serializable snapshot of grid data for <see cref="PersistentComponentState"/>
    /// transfer from server pre-render to interactive mode.
    /// </summary>
    private sealed record PersistedGridState(List<TDto> Items, int TotalItems);
}
