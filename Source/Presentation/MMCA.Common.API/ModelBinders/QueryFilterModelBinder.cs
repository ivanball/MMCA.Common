using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace MMCA.Common.API.ModelBinders;

/// <summary>
/// Custom model binder that parses structured query string filters into a dictionary of
/// property-name to (operator, value) pairs.
/// </summary>
/// <remarks>
/// <para>Expected query string format for each filter:</para>
/// <code>
/// ?filters[PropertyName].operator=eq&amp;filters[PropertyName].value=SomeValue
/// </code>
/// <para>
/// Multiple properties can be filtered simultaneously. Incomplete entries (a missing operator,
/// or a missing value for an operator other than the value-less IS EMPTY / IS NOT EMPTY) are
/// silently discarded. Property name matching is case-insensitive.
/// </para>
/// <para>
/// Example: <c>?filters[Name].operator=contains&amp;filters[Name].value=shirt&amp;filters[Price].operator=gte&amp;filters[Price].value=10</c>
/// produces two filter entries: Name (contains, "shirt") and Price (gte, "10").
/// </para>
/// </remarks>
public sealed class QueryFilterModelBinder : IModelBinder
{
    /// <summary>
    /// Maximum number of distinct filter properties honored on one request. Well past any real
    /// grid (the widest DTO in either app is nowhere near this), and it bounds the per-request
    /// reflection work a caller can demand from <c>QueryFilterService</c>, which resolves each
    /// unknown name by reflection rather than memoizing misses. Surplus entries are dropped rather
    /// than rejected: they were never valid filters to begin with, and a 400 here would be a
    /// breaking change for any client that happens to send junk alongside real filters.
    /// </summary>
    public const int MaxFilters = 50;

    /// <inheritdoc />
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var query = bindingContext.HttpContext.Request.Query;
        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase);

        // Operator and value keys for the same property may arrive in any order,
        // so we accumulate both parts and merge them into tuples
        foreach (var key in query.Keys)
        {
            if (!IsFilterKey(key))
                continue;

            var property = GetFilterPropertyName(key);
            if (property is null)
                continue;

            var suffix = GetFilterSuffix(key);
            if (suffix is null)
                continue;

            if (!filters.TryGetValue(property, out var tuple))
            {
                if (filters.Count >= MaxFilters)
                    continue;

                tuple = (string.Empty, string.Empty);
            }

            var value = query[key].ToString();
            filters[property] = suffix == "operator"
                ? (value, tuple.Value)
                : (tuple.Operator, value);
        }

        // Remove incomplete filter entries: a missing operator, or a missing value for an operator
        // that compares against one. IS EMPTY / IS NOT EMPTY take no value, so a grid sends the
        // operator alone and that entry is complete as it stands.
        foreach (var key in filters
            .Where(f => IsIncomplete(f.Value))
            .Select(f => f.Key)
            .ToList())
        {
            filters.Remove(key);
        }

        bindingContext.Result = ModelBindingResult.Success(filters);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether a bound entry cannot be applied: no operator, or no value for an operator that
    /// compares against one.
    /// </summary>
    /// <param name="filter">The bound operator and value.</param>
    /// <returns><see langword="true"/> when the entry is to be discarded.</returns>
    private static bool IsIncomplete((string Operator, string Value) filter) =>
        string.IsNullOrEmpty(filter.Operator)
        || string.IsNullOrEmpty(filter.Value) && !IsValueLessOperator(filter.Operator);

    /// <summary>
    /// Whether <paramref name="op"/> is a presence check that takes no value (IS EMPTY, IS NOT EMPTY,
    /// in any casing, so MudBlazor's lower-case <c>"is empty"</c> counts too).
    /// </summary>
    /// <param name="op">The operator as sent.</param>
    /// <returns><see langword="true"/> when the operator is complete without a value.</returns>
    private static bool IsValueLessOperator(string op)
    {
        var trimmed = op.Trim();

        return trimmed.Equals("IS EMPTY", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("IS NOT EMPTY", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a query string key matches the <c>filters[...].operator</c> or <c>filters[...].value</c> pattern.
    /// </summary>
    /// <param name="key">The query string key to check.</param>
    /// <returns><see langword="true"/> if the key is a recognized filter key.</returns>
    private static bool IsFilterKey(string key) =>
        key.StartsWith("filters[", StringComparison.OrdinalIgnoreCase)
        && (key.EndsWith("].operator", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("].value", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Extracts the property name from between the brackets in a filter key (e.g., "Name" from "filters[Name].operator").
    /// </summary>
    /// <param name="key">The filter query string key.</param>
    /// <returns>The property name, or <see langword="null"/> if the key is malformed.</returns>
    private static string? GetFilterPropertyName(string key)
    {
        var startIndex = "filters[".Length;
        var endIndex = key.IndexOf(']', startIndex);
        if (endIndex < 0)
            return null;

        return key[startIndex..endIndex];
    }

    /// <summary>
    /// Extracts the suffix ("operator" or "value") from the filter key.
    /// </summary>
    /// <param name="key">The filter query string key.</param>
    /// <returns>The suffix string, or <see langword="null"/> if unrecognized.</returns>
    private static string? GetFilterSuffix(string key)
    {
        if (key.EndsWith("].operator", StringComparison.OrdinalIgnoreCase))
            return "operator";

        if (key.EndsWith("].value", StringComparison.OrdinalIgnoreCase))
            return "value";

        return null;
    }
}
