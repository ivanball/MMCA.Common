namespace MMCA.Common.Shared.Http;

/// <summary>
/// The key convention of the <c>filters[Key].operator</c> / <c>filters[Key].value</c> query string,
/// shared by the UI that writes it and the server that resolves it.
/// <para>
/// A filter key normally names one property, and the filter dictionary holds one entry per key. An
/// ALIASED key (<c>Name~search</c>) carries a second filter on the same property: everything before
/// <see cref="AliasSeparator"/> is the property, and the server resolves it exactly as it resolves the
/// bare key and ANDs the two predicates. A list page uses it when its search box maps onto a column
/// that already carries the grid's own filter, so neither one overwrites the other.
/// </para>
/// </summary>
/// <remarks>
/// Lives in Shared because both ends need the same literal: the server's filter resolution
/// (<c>MMCA.Common.Application</c>) strips it and the UI list page base (<c>MMCA.Common.UI</c>) writes
/// it, and those packages have no reference to one another. <c>~</c> is an unreserved URL character
/// that no property path or server-mapped expression contains.
/// </remarks>
public static class QueryFilterKeys
{
    /// <summary>The character that separates the property from the alias tag in a filter key.</summary>
    public const char AliasSeparator = '~';

    /// <summary>The alias tag a list page's search box uses.</summary>
    public const string SearchTag = "search";

    /// <summary>
    /// Builds the aliased key for a second filter on <paramref name="property"/>.
    /// </summary>
    /// <param name="property">The property (or DTO field) the filter applies to.</param>
    /// <param name="tag">A tag that tells this filter apart from the property's own filter.</param>
    /// <returns>The aliased key, for example <c>Name~search</c>.</returns>
    public static string Alias(string property, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        return string.Concat(PropertyOf(property), AliasSeparator.ToString(), tag);
    }

    /// <summary>
    /// The property a filter key names: the key itself, or the part before
    /// <see cref="AliasSeparator"/> for an aliased key.
    /// </summary>
    /// <param name="key">The filter key.</param>
    /// <returns>The property the filter applies to.</returns>
    public static string PropertyOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var separator = key.IndexOf(AliasSeparator, StringComparison.Ordinal);
        return separator < 0 ? key : key[..separator];
    }
}
