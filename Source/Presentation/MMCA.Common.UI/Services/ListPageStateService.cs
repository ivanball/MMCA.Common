using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Snapshot of a list page's state for restoration after navigation.
/// Immutable — use <c>with</c> expressions to update.
/// </summary>
public sealed record ListPageState
{
    /// <summary>MudDataGrid 0-indexed page number.</summary>
    public int Page { get; init; }

    /// <summary>Rows per page selected by the user.</summary>
    public int PageSize { get; init; }

    /// <summary>Mobile card list 1-indexed page number.</summary>
    public int MobilePage { get; init; } = 1;

    /// <summary>Document scroll offset in pixels (from <c>document.scrollingElement.scrollTop</c>).</summary>
    public double ScrollPosition { get; init; }

    /// <summary>
    /// MudDataGrid sort column (the <c>SortBy</c> property name from the active <c>SortDefinition</c>).
    /// <see langword="null"/> or empty when no sort is applied.
    /// </summary>
    public string? SortColumn { get; init; }

    /// <summary>
    /// <see langword="true"/> when the active sort is descending; <see langword="false"/> otherwise.
    /// Ignored when <see cref="SortColumn"/> is <see langword="null"/> or empty.
    /// </summary>
    public bool SortDescending { get; init; }

    /// <summary>
    /// Named filter values (e.g., "search" → "shirt", "status" → "Accepted").
    /// Keys are page-specific; each page decides what to save/restore.
    /// </summary>
    public IReadOnlyDictionary<string, string> Filters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Per-circuit scoped service that preserves list page state (page number, page size,
/// scroll position, search/filter values) across in-app navigation. The synchronous
/// dictionary is the in-memory fast path; <see cref="HydrateFromSessionAsync"/> /
/// <see cref="PersistToSessionAsync"/> mirror entries through <c>sessionStorage</c>
/// (via the <c>nav-interop.js</c> module) so state survives circuit teardowns,
/// <c>forceLoad: true</c> navigations, and the SSR → WASM render-mode transition.
/// </summary>
public sealed class ListPageStateService(IJSRuntime js)
{
    private const string ModulePath = "./_content/MMCA.Common.UI/nav-interop.js";
    private const string SessionKeyPrefix = "mmca.lps:";

    private readonly Dictionary<string, ListPageState> _states = [];
    private IJSObjectReference? _module;

    /// <summary>
    /// Returns the in-memory snapshot for <paramref name="routePath"/>, or
    /// <see langword="null"/> if none has been saved this session. Synchronous and
    /// safe to call from <c>OnInitialized</c> during SSR prerender.
    /// </summary>
    public ListPageState? GetState(string routePath) =>
        _states.GetValueOrDefault(routePath);

    /// <summary>
    /// Stores <paramref name="state"/> in the in-memory dictionary keyed by
    /// <paramref name="routePath"/>. Use <see cref="PersistToSessionAsync"/> to
    /// also write through to <c>sessionStorage</c>.
    /// </summary>
    public void SaveState(string routePath, ListPageState state) =>
        _states[routePath] = state;

    /// <summary>
    /// Updates only the scroll position for the given route, preserving all other fields.
    /// Creates a minimal entry if none exists yet (e.g., the user scrolls before the grid
    /// has fired its first <c>ServerData</c> save).
    /// </summary>
    public void UpdateScrollPosition(string routePath, double scrollPosition) =>
        _states[routePath] = _states.TryGetValue(routePath, out var existing)
            ? existing with { ScrollPosition = scrollPosition }
            : new ListPageState { ScrollPosition = scrollPosition };

    /// <summary>
    /// Loads any persisted state for <paramref name="routePath"/> from
    /// <c>sessionStorage</c> into the in-memory dictionary. Should be called from
    /// <c>OnAfterRenderAsync(firstRender: true)</c> — JS interop is unavailable during
    /// SSR prerender and the call is silently a no-op in that case.
    /// </summary>
    public async ValueTask HydrateFromSessionAsync(string routePath)
    {
        try
        {
            var module = await GetModuleAsync().ConfigureAwait(false);
            if (module is null)
            {
                return;
            }

            var persisted = await module.InvokeAsync<ListPageState?>("sessionGet", SessionKeyPrefix + routePath).ConfigureAwait(false);
            if (persisted is not null)
            {
                _states[routePath] = persisted;
            }
        }
        catch (InvalidOperationException)
        {
            // Prerender / SSR — JS interop not yet available.
        }
        catch (JSDisconnectedException)
        {
            // Circuit torn down between hydration calls — ignore.
        }
        catch (JSException)
        {
            // Defensive: never let storage failures break the calling page.
        }
    }

    /// <summary>
    /// Writes the current in-memory state for <paramref name="routePath"/> through
    /// to <c>sessionStorage</c>. Silently no-ops during SSR prerender and on storage
    /// failures (Safari Private mode, quota exceeded, etc.).
    /// </summary>
    public async ValueTask PersistToSessionAsync(string routePath)
    {
        if (!_states.TryGetValue(routePath, out var state))
        {
            return;
        }

        try
        {
            var module = await GetModuleAsync().ConfigureAwait(false);
            if (module is null)
            {
                return;
            }

            await module.InvokeVoidAsync("sessionSet", SessionKeyPrefix + routePath, state).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Prerender / SSR — JS interop not yet available.
        }
        catch (JSDisconnectedException)
        {
            // Circuit torn down — ignore.
        }
        catch (JSException)
        {
            // Defensive: never let storage failures break the calling page.
        }
    }

    private async ValueTask<IJSObjectReference?> GetModuleAsync()
    {
        if (_module is not null)
        {
            return _module;
        }

        try
        {
            _module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath).ConfigureAwait(false);
            return _module;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
    }
}
