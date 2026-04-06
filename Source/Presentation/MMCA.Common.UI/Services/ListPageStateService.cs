namespace MMCA.Common.UI.Services;

/// <summary>
/// Snapshot of a list page's state for restoration after navigation.
/// Immutable — create a new instance when state changes.
/// </summary>
public sealed class ListPageState
{
    /// <summary>MudDataGrid 0-indexed page number.</summary>
    public int Page { get; init; }

    /// <summary>Rows per page selected by the user.</summary>
    public int PageSize { get; init; }

    /// <summary>Mobile card list 1-indexed page number.</summary>
    public int MobilePage { get; init; } = 1;

    /// <summary>
    /// Named filter values (e.g., "search" → "shirt", "status" → "Accepted").
    /// Keys are page-specific; each page decides what to save/restore.
    /// </summary>
    public IReadOnlyDictionary<string, string> Filters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Per-circuit scoped service that preserves list page state (page number, page size,
/// search/filter values) across in-app navigation. State is keyed by route path.
/// Lost on full page refresh (circuit teardown), which is consistent with Blazor Server behavior.
/// </summary>
public sealed class ListPageStateService
{
    private readonly Dictionary<string, ListPageState> _states = [];

    public ListPageState? GetState(string routePath) =>
        _states.GetValueOrDefault(routePath);

    public void SaveState(string routePath, ListPageState state) =>
        _states[routePath] = state;
}
