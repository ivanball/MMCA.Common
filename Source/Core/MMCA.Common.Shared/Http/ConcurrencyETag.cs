using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Shared.Http;

/// <summary>
/// Translates between the framework's optimistic-concurrency token (the EF Core <c>rowversion</c>
/// byte array carried by <see cref="DTOs.IConcurrencyAware"/>) and the HTTP entity tag that
/// represents it on the wire. It lives in Shared because both ends of the exchange need it: the API
/// reads an <c>If-Match</c> value with it and the UI services write one with it.
/// </summary>
/// <remarks>
/// <para>
/// The tag is always WEAK (<c>W/"..."</c>). A strong tag promises byte-for-byte equality of the
/// representation, and this one does not: the same row version renders differently under a
/// <c>fields=</c> projection, and it says nothing at all about the serializer's formatting. Weak is
/// the honest strength for a token that answers "is this the same version of the resource", which is
/// exactly what <c>If-Match</c> asks.
/// </para>
/// <para>
/// The payload is the base64 of the raw token, so the round trip is lossless and the value stays
/// inside the quoted-string grammar RFC 9110 defines for an entity tag.
/// </para>
/// </remarks>
public static class ConcurrencyETag
{
    /// <summary>The request header a conditional write carries.</summary>
    public const string IfMatchHeaderName = "If-Match";

    /// <summary>The response header a versioned read carries.</summary>
    public const string ETagHeaderName = "ETag";

    /// <summary>The <c>If-Match</c> value that matches any current version.</summary>
    public const string Wildcard = "*";

    /// <summary>
    /// Renders a row version as the weak entity tag a client echoes back in <c>If-Match</c>.
    /// </summary>
    /// <param name="rowVersion">The raw concurrency token.</param>
    /// <returns>The entity tag, e.g. <c>W/"AAAAAAAAB9E="</c>.</returns>
    public static string Format(byte[] rowVersion)
    {
        ArgumentNullException.ThrowIfNull(rowVersion);

        return string.Concat("W/\"", Convert.ToBase64String(rowVersion), "\"");
    }

    /// <summary>
    /// Reads a row version back out of an entity tag, tolerating the weak prefix and the quotes.
    /// </summary>
    /// <param name="value">The header value, e.g. <c>W/"AAAAAAAAB9E="</c> or a bare quoted tag.</param>
    /// <param name="rowVersion">The decoded token when parsing succeeds; otherwise null.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="value"/> yielded a concrete, non-empty token.
    /// A blank value, the <see cref="Wildcard"/>, and anything that is not base64 all return
    /// <see langword="false"/>: the caller decides which of those is an error in its context.
    /// </returns>
    /// <remarks>
    /// Only the FIRST tag of a comma-separated list is considered. A conditional write is a
    /// single-version precondition in this framework (there is one row version to compare against),
    /// so a list beyond its first entry has no meaning here.
    /// </remarks>
    public static bool TryParse(string? value, [NotNullWhen(true)] out byte[]? rowVersion)
    {
        rowVersion = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.AsSpan();

        var comma = candidate.IndexOf(',');
        if (comma >= 0)
        {
            candidate = candidate[..comma];
        }

        candidate = candidate.Trim();

        if (candidate.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[2..].Trim();
        }

        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1];
        }

        if (candidate.IsEmpty)
        {
            return false;
        }

        var buffer = new byte[(candidate.Length + 3) / 4 * 3];
        if (!Convert.TryFromBase64Chars(candidate, buffer, out var written) || written == 0)
        {
            return false;
        }

        rowVersion = buffer[..written];
        return true;
    }
}
