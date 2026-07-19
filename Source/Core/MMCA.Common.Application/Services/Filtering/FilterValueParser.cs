namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Parses the comma-separated value list carried by the <c>IN</c> filter operator into a typed
/// list. Values that fail to parse are skipped (consistent with the single-value strategies, which
/// silently ignore an unparseable value), so a malformed entry never fails the whole request.
/// </summary>
internal static class FilterValueParser
{
    private static readonly char[] Separator = [','];

    /// <summary>Parses a comma-separated list of value-type items (e.g. int, Guid).</summary>
    /// <typeparam name="T">The value type each item parses to.</typeparam>
    /// <param name="value">The raw comma-separated value.</param>
    /// <param name="parse">Parses one item, returning <see langword="null"/> when it is unparseable.</param>
    /// <returns>The successfully parsed items, in order.</returns>
    public static List<T> ParseList<T>(string value, Func<string, T?> parse)
        where T : struct
    {
        var result = new List<T>();
        if (string.IsNullOrWhiteSpace(value))
            return result;

        foreach (var part in value.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (parse(part) is { } parsed)
                result.Add(parsed);
        }

        return result;
    }

    /// <summary>Parses a comma-separated list of non-empty strings.</summary>
    public static List<string> ParseStringList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return [.. value.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
