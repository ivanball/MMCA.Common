using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Primitives;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Reads and writes URL query strings the way ASP.NET Core's <c>QueryHelpers.ParseQuery</c> and
/// <c>QueryHelpers.AddQueryString</c> do, without the <c>Microsoft.AspNetCore.WebUtilities</c> package
/// (this project stays WebAssembly-safe and carries no ASP.NET Core server dependency).
/// </summary>
/// <remarks>
/// <para>
/// Parsing: one leading <c>?</c> is skipped; pairs are split on <c>&amp;</c> only; empty segments are
/// skipped; a segment without <c>=</c> is a key with an empty value; the key ends at the first
/// <c>=</c>; <c>+</c> decodes to a space before percent-decoding with
/// <see cref="Uri.UnescapeDataString(string)"/>; keys compare case-insensitively (ordinal) and a
/// repeated key accumulates its values in order. A <c>#</c> is not special, so pass the query part
/// only.
/// </para>
/// <para>
/// Building: a pair with a <see langword="null"/> value is skipped; keys and values are encoded with
/// <see cref="UrlEncoder.Default"/>; the first pair starts with <c>?</c> unless the URI already has a
/// query, and the pairs go in before any <c>#fragment</c>.
/// </para>
/// </remarks>
internal static class QueryStringCodec
{
    /// <summary>Parses a query string (with or without its leading <c>?</c>).</summary>
    /// <param name="queryString">The raw, still-encoded query string.</param>
    /// <returns>The decoded keys and values; empty when there are none.</returns>
    public static Dictionary<string, StringValues> Parse(string? queryString)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var remaining = (queryString ?? string.Empty).AsSpan();
        if (remaining.StartsWith('?'))
        {
            remaining = remaining[1..];
        }

        while (!remaining.IsEmpty)
        {
            var ampersand = remaining.IndexOf('&');
            var segment = ampersand >= 0 ? remaining[..ampersand] : remaining;
            remaining = ampersand >= 0 ? remaining[(ampersand + 1)..] : [];

            if (!segment.IsEmpty)
            {
                Append(values, segment);
            }
        }

        var result = new Dictionary<string, StringValues>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, list) in values)
        {
            result.Add(name, list.Count == 1 ? new StringValues(list[0]) : new StringValues([.. list]));
        }

        return result;
    }

    /// <summary>
    /// Appends the pairs to <paramref name="uri"/> as query parameters, before any fragment, skipping
    /// pairs whose value is <see langword="null"/>.
    /// </summary>
    /// <param name="uri">The base URI, which may already carry a query and a fragment.</param>
    /// <param name="parameters">The parameters to append, in order.</param>
    /// <returns>The combined URI.</returns>
    public static string AddQueryString(string uri, IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(parameters);

        var fragmentIndex = uri.IndexOf('#', StringComparison.Ordinal);
        var head = fragmentIndex >= 0 ? uri[..fragmentIndex] : uri;
        var fragment = fragmentIndex >= 0 ? uri[fragmentIndex..] : string.Empty;
        var hasQuery = head.Contains('?', StringComparison.Ordinal);

        var builder = new StringBuilder(head);
        foreach (var (key, value) in parameters)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(hasQuery ? '&' : '?')
                .Append(UrlEncoder.Default.Encode(key))
                .Append('=')
                .Append(UrlEncoder.Default.Encode(value));
            hasQuery = true;
        }

        return builder.Append(fragment).ToString();
    }

    /// <summary>Adds one non-empty <c>name[=value]</c> segment; the name ends at the first <c>=</c>.</summary>
    private static void Append(Dictionary<string, List<string>> values, ReadOnlySpan<char> segment)
    {
        var equals = segment.IndexOf('=');
        var name = Decode(equals >= 0 ? segment[..equals] : segment);
        var value = equals >= 0 ? Decode(segment[(equals + 1)..]) : string.Empty;

        if (!values.TryGetValue(name, out var list))
        {
            list = [];
            values.Add(name, list);
        }

        list.Add(value);
    }

    private static string Decode(ReadOnlySpan<char> encoded) =>
        Uri.UnescapeDataString(encoded.ToString().Replace('+', ' '));
}
