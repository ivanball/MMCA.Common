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
    /// Named filter values (e.g., "search" → "shirt", "status" → "Accepted").
    /// Keys are page-specific; each page decides what to save/restore.
    /// </summary>
    public IReadOnlyDictionary<string, string> Filters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Per-circuit scoped service that preserves list page state (page number, page size,
/// scroll position, search/filter values) across in-app navigation. State is keyed by
/// route path. Lost on full page refresh (circuit teardown), which is consistent with
/// Blazor Server behavior.
/// </summary>
public sealed class ListPageStateService
{
    private readonly Dictionary<string, ListPageState> _states = [];

    public ListPageState? GetState(string routePath) =>
        _states.GetValueOrDefault(routePath);

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
}
