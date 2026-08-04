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

    /// <summary>
    /// Whether a value-type strategy can apply <paramref name="value"/> under <paramref name="op"/>,
    /// following the shape every built-in strategy shares: presence checks ignore the value, IN needs
    /// at least one parseable item, BETWEEN needs exactly two segments that both parse, and every
    /// other operator needs the single scalar to parse.
    /// </summary>
    /// <typeparam name="T">The value type each item parses to.</typeparam>
    /// <param name="op">The operator, already uppercased by the caller.</param>
    /// <param name="value">The raw filter value.</param>
    /// <param name="parse">Parses one item, returning <see langword="null"/> when it is unparseable.</param>
    /// <returns><see langword="true"/> when the value is usable for that operator.</returns>
    public static bool CanParse<T>(string op, string value, Func<string, T?> parse)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(parse);

        return op switch
        {
            "IS EMPTY" or "IS NOT EMPTY" => true,
            "IN" => ParseList(value, parse).Count > 0,
            "BETWEEN" => HasExactlyTwoBounds(value, parse),
            _ => parse(value) is not null,
        };
    }

    /// <summary>
    /// Whether <paramref name="value"/> is exactly two comma-separated segments that both parse.
    /// </summary>
    /// <remarks>
    /// Deliberately does not use <see cref="StringSplitOptions.RemoveEmptyEntries"/> and does not go
    /// through <see cref="ParseList{T}(string, Func{string, T?})"/>: dropping unparseable and empty
    /// segments let "5,abc,10" and "5,,10" validate as a two-bound range, and the strategies then
    /// applied the surviving pair as the bounds the caller never asked for.
    /// </remarks>
    private static bool HasExactlyTwoBounds<T>(string value, Func<string, T?> parse)
        where T : struct
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split(Separator, StringSplitOptions.TrimEntries);

        return parts.Length == 2 && parse(parts[0]) is not null && parse(parts[1]) is not null;
    }
}
