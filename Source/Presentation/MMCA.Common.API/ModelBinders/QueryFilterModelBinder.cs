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
/// Multiple properties can be filtered simultaneously. Incomplete entries (missing either
/// the operator or value component) are silently discarded. Property name matching is
/// case-insensitive.
/// </para>
/// <para>
/// Example: <c>?filters[Name].operator=contains&amp;filters[Name].value=shirt&amp;filters[Price].operator=gte&amp;filters[Price].value=10</c>
/// produces two filter entries: Name (contains, "shirt") and Price (gte, "10").
/// </para>
/// </remarks>
public sealed class QueryFilterModelBinder : IModelBinder
{
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
                tuple = (string.Empty, string.Empty);

            var value = query[key].ToString();
            filters[property] = suffix == "operator"
                ? (value, tuple.Value)
                : (tuple.Operator, value);
        }

        // Remove incomplete filter entries missing operator or value
        foreach (var key in filters
            .Where(f => string.IsNullOrEmpty(f.Value.Operator) || string.IsNullOrEmpty(f.Value.Value))
            .Select(f => f.Key)
            .ToList())
        {
            filters.Remove(key);
        }

        bindingContext.Result = ModelBindingResult.Success(filters);
        return Task.CompletedTask;
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
