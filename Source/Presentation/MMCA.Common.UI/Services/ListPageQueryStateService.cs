using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Encodes <see cref="ListPageState"/> into URL query strings and decodes them back.
/// Two-way binding between MudDataGrid list pages and the browser address bar so that
/// browser back/forward, refresh, and shareable links all restore filter, sort, and
/// pagination state deterministically.
/// </summary>
/// <remarks>
/// <para>Reserved query keys (kept short on purpose — these end up in shareable links):</para>
/// <list type="bullet">
///   <item><description><c>p</c> = 0-indexed desktop page</description></item>
///   <item><description><c>ps</c> = page size</description></item>
///   <item><description><c>mp</c> = 1-indexed mobile page</description></item>
///   <item><description><c>s</c> = sort column property name</description></item>
///   <item><description><c>sd</c> = sort direction (<c>desc</c> only — ascending is the default and omitted)</description></item>
///   <item><description><c>q</c> = free-text search (extracted from <see cref="ListPageState.Filters"/> by convention)</description></item>
///   <item><description><c>f:&lt;name&gt;</c> = any other named filter</description></item>
/// </list>
/// <para>Default values are omitted from the URL so pristine list pages have clean URLs.</para>
/// </remarks>
public sealed class ListPageQueryStateService(NavigationManager navigation)
{
    private const string PageKey = "p";
    private const string PageSizeKey = "ps";
    private const string MobilePageKey = "mp";
    private const string SortKey = "s";
    private const string SortDirKey = "sd";
    private const string SearchKey = "q";
    private const string FilterPrefix = "f:";
    private const string SearchFilterName = "search";
    private const string DescendingMarker = "desc";

    /// <summary>
    /// Reads list-page state from the current <see cref="NavigationManager.Uri"/>.
    /// </summary>
    public ListPageState ReadCurrent()
    {
        var absolute = navigation.ToAbsoluteUri(navigation.Uri);
        return ParseQueryString(absolute.Query);
    }

    /// <summary>
    /// Parses a raw query string (with or without a leading <c>?</c>) into a
    /// <see cref="ListPageState"/>. Pure helper exposed for unit testing without a
    /// <see cref="NavigationManager"/>.
    /// </summary>
    public static ListPageState ParseQueryString(string? queryString)
    {
        var parsed = QueryHelpers.ParseQuery(queryString ?? string.Empty);

        var page = TryGetInt(parsed, PageKey, defaultValue: 0);
        var pageSize = TryGetInt(parsed, PageSizeKey, defaultValue: 0);
        var mobilePage = TryGetInt(parsed, MobilePageKey, defaultValue: 1);

        string? sortColumn = null;
        if (parsed.TryGetValue(SortKey, out var sortValues))
        {
            var sortValue = sortValues.ToString();
            if (!string.IsNullOrWhiteSpace(sortValue))
            {
                sortColumn = sortValue;
            }
        }

        var sortDescending = false;
        if (parsed.TryGetValue(SortDirKey, out var sortDirValues))
        {
            sortDescending = string.Equals(sortDirValues.ToString(), DescendingMarker, StringComparison.OrdinalIgnoreCase);
        }

        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, valuesAcrossDuplicates) in parsed)
        {
            var value = valuesAcrossDuplicates.ToString();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (string.Equals(key, SearchKey, StringComparison.Ordinal))
            {
                filters[SearchFilterName] = value;
            }
            else if (key.StartsWith(FilterPrefix, StringComparison.Ordinal) && key.Length > FilterPrefix.Length)
            {
                filters[key[FilterPrefix.Length..]] = value;
            }
        }

        return new ListPageState
        {
            Page = page,
            PageSize = pageSize,
            MobilePage = mobilePage,
            SortColumn = sortColumn,
            SortDescending = sortDescending,
            Filters = filters,
        };
    }

    /// <summary>
    /// Builds a relative path string for the supplied <paramref name="basePath"/> with
    /// <paramref name="state"/> encoded as query parameters. Default values (page 0,
    /// mobile page 1, no sort, no filters) are omitted so pristine pages have clean URLs.
    /// </summary>
    public static string BuildPath(string basePath, ListPageState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(basePath);
        ArgumentNullException.ThrowIfNull(state);

        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (state.Page > 0)
        {
            parameters[PageKey] = state.Page.ToString(CultureInfo.InvariantCulture);
        }

        if (state.PageSize > 0)
        {
            parameters[PageSizeKey] = state.PageSize.ToString(CultureInfo.InvariantCulture);
        }

        if (state.MobilePage > 1)
        {
            parameters[MobilePageKey] = state.MobilePage.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(state.SortColumn))
        {
            parameters[SortKey] = state.SortColumn;
            if (state.SortDescending)
            {
                parameters[SortDirKey] = DescendingMarker;
            }
        }

        foreach (var (name, value) in state.Filters)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (string.Equals(name, SearchFilterName, StringComparison.Ordinal))
            {
                parameters[SearchKey] = value;
            }
            else
            {
                parameters[FilterPrefix + name] = value;
            }
        }

        return parameters.Count == 0
            ? basePath
            : QueryHelpers.AddQueryString(basePath, parameters);
    }

    /// <summary>
    /// Replaces the current browser history entry with one whose URL reflects
    /// <paramref name="state"/>. Uses <see cref="NavigationOptions.ReplaceHistoryEntry"/>
    /// so filter changes do not pollute the back stack.
    /// </summary>
    public void ReplaceState(ListPageState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var basePath = navigation.ToAbsoluteUri(navigation.Uri).GetLeftPart(UriPartial.Path);
        var target = BuildPath(basePath, state);
        navigation.NavigateTo(target, new NavigationOptions { ReplaceHistoryEntry = true });
    }

    private static int TryGetInt(Dictionary<string, StringValues> parsed, string key, int defaultValue)
    {
        if (!parsed.TryGetValue(key, out var values))
        {
            return defaultValue;
        }

        return int.TryParse(values.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt)
            ? parsedInt
            : defaultValue;
    }
}
