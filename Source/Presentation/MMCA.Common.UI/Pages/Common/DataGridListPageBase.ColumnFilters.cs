using System.Globalization;
using MMCA.Common.UI.Services;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Common;

/// <content>
/// Column-filter persistence: the grid's own column filters are saved with the rest of the list
/// state (URL <c>c:</c> keys, in-memory state and <c>sessionStorage</c>) and put back on the grid
/// when the page is restored, so a filter survives opening a row and pressing Back, a refresh and a
/// shared link (Store Run 4 AI-31).
/// </content>
/// <remarks>
/// Restoring follows the restored sort: the grid's first load starts before the first-render setup
/// can touch the grid, so while a restore is pending that load sends the restored filters itself
/// (<see cref="ResolveColumnFilters"/>), and the setup then puts the same filters on the grid
/// (<see cref="ApplyRestoredColumnFiltersToGrid"/>) without a second load. A filter on a column the
/// grid does not render, or whose value no longer parses, is dropped rather than sent to the API.
/// </remarks>
public abstract partial class DataGridListPageBase<TDto>
{
    private IReadOnlyDictionary<string, ColumnFilterState> _savedColumnFilters = new Dictionary<string, ColumnFilterState>(StringComparer.Ordinal);

    // True while column filters restored from the URL or saved state are not yet on the grid.
    private bool _restoredColumnFiltersPending;

    /// <summary>Takes the column filters restored from the URL or saved state and marks them pending for the grid.</summary>
    /// <param name="state">The restored list state.</param>
    private void RestoreColumnFilters(ListPageState state)
    {
        _savedColumnFilters = state.ColumnFilters;
        _restoredColumnFiltersPending = state.ColumnFilters.Count > 0
            || GridRef is { FilterDefinitions.Count: > 0 };
    }

    /// <summary>
    /// The filter definitions a fetch sends and the column filters it saves. While a restore is
    /// pending these are the restored filters (built against the rendered grid, so unknown columns
    /// drop out); otherwise the grid's own.
    /// </summary>
    /// <param name="gridDefinitions">The grid's filter definitions from its state.</param>
    /// <param name="restorePendingAtLoadStart">
    /// Whether a restore was pending when the fetch STARTED. The grid's state is a copy taken then, so
    /// when the first-render setup put the restored filters on the grid (clearing the pending flag)
    /// while the fetch awaited <see cref="OnBeforeFetchAsync"/>, that copy still holds none: the fetch
    /// must send the restored filters anyway, or the restored <c>c:</c> filter was neither sent nor
    /// kept in the URL (Run 18 O-85, X-34).
    /// </param>
    private (IEnumerable<IFilterDefinition<TDto>>? Definitions, IReadOnlyDictionary<string, ColumnFilterState> Saved) ResolveColumnFilters(
        IEnumerable<IFilterDefinition<TDto>>? gridDefinitions,
        bool restorePendingAtLoadStart = false)
    {
        if (!_restoredColumnFiltersPending && !restorePendingAtLoadStart)
        {
            return (gridDefinitions, SnapshotColumnFilters(gridDefinitions));
        }

        // No grid yet to build definitions against: keep the restored filters saved as they are.
        if (GridRef is not { } grid)
        {
            return (gridDefinitions, _savedColumnFilters);
        }

        var restored = BuildColumnFilterDefinitions(grid, _savedColumnFilters);
        return (restored, SnapshotColumnFilters(restored));
    }

    /// <summary>
    /// Replaces the grid's filter definitions with the pending restored ones (an empty restore clears
    /// them, which is what Back to an unfiltered URL means). Adding to the list directly, rather than
    /// through <c>AddFilterAsync</c>, keeps the filter panel closed and starts no load: the caller
    /// decides whether to reload.
    /// </summary>
    /// <param name="grid">The rendered grid.</param>
    private void ApplyRestoredColumnFiltersToGrid(MudDataGrid<TDto> grid)
    {
        if (!_restoredColumnFiltersPending)
        {
            return;
        }

        _restoredColumnFiltersPending = false;
        var restored = BuildColumnFilterDefinitions(grid, _savedColumnFilters);
        grid.FilterDefinitions.Clear();
        grid.FilterDefinitions.AddRange(restored);
    }

    /// <summary>
    /// The saved form of <paramref name="definitions"/>: one filter per column by the same rule the
    /// fetch uses (<see cref="EffectiveColumnFilters"/>), values written in the invariant culture.
    /// </summary>
    private static Dictionary<string, ColumnFilterState> SnapshotColumnFilters(IEnumerable<IFilterDefinition<TDto>>? definitions) =>
        EffectiveColumnFilters(definitions).ToDictionary(
            definition => definition.Column!.PropertyName!,
            definition => new ColumnFilterState(definition.Operator!, FormatFilterValue(definition.Value)),
            StringComparer.Ordinal);

    /// <summary>
    /// Builds a filter definition for each saved filter whose column the grid renders and allows
    /// filtering on, with its value parsed back to the column's type.
    /// </summary>
    private static List<IFilterDefinition<TDto>> BuildColumnFilterDefinitions(
        MudDataGrid<TDto> grid,
        IReadOnlyDictionary<string, ColumnFilterState> saved)
    {
        var definitions = new List<IFilterDefinition<TDto>>(saved.Count);
        foreach (var (property, filter) in saved)
        {
            var column = grid.RenderedColumns.FirstOrDefault(c =>
                string.Equals(c.PropertyName, property, StringComparison.Ordinal) && (c.Filterable ?? grid.Filterable));
            if (column is null)
            {
                continue;
            }

            var definition = new FilterDefinition<TDto> { Column = column, Operator = filter.Operator, Title = column.Title };
            if (filter.Value is not null)
            {
                if (!TryParseFilterValue(definition.FieldType, filter.Value, out var value))
                {
                    continue;
                }

                definition.Value = value;
            }

            definitions.Add(definition);
        }

        return definitions;
    }

    private static string? FormatFilterValue(object? value) => value switch
    {
        null => null,
        string text => text,
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>Parses a saved filter value back to the column's type; false when it no longer parses.</summary>
    private static bool TryParseFilterValue(FieldType fieldType, string text, out object? value)
    {
        var type = fieldType.InnerType;
        value = null;
        if (fieldType.IsString || type is null)
        {
            value = text;
            return true;
        }

        if (fieldType.IsBoolean)
        {
            var parsed = bool.TryParse(text, out var flag);
            value = flag;
            return parsed;
        }

        if (fieldType.IsEnum)
        {
            var parsed = Enum.TryParse(type, text, ignoreCase: true, out var member);
            value = member;
            return parsed;
        }

        if (fieldType.IsGuid)
        {
            var parsed = Guid.TryParse(text, out var guid);
            value = guid;
            return parsed;
        }

        return TryParseDateOrNumber(fieldType, type, text, out value);
    }

    private static bool TryParseDateOrNumber(FieldType fieldType, Type type, string text, out object? value)
    {
        value = null;
        if (fieldType.IsDateOnly)
        {
            var parsed = DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var date);
            value = date;
            return parsed;
        }

        if (fieldType.IsDateTime)
        {
            var parsed = DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime);
            value = dateTime;
            return parsed;
        }

        if (fieldType.IsNumber)
        {
            try
            {
                value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
            {
                return false;
            }
        }

        value = text;
        return true;
    }
}
