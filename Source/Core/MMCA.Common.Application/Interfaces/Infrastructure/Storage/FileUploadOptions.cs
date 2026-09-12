using System.Globalization;
using System.Text;

namespace MMCA.Common.Application.Interfaces.Infrastructure.Storage;

/// <summary>
/// Response headers stored alongside an uploaded blob (ADR-045). Managed storage keeps the bytes, but
/// how a browser treats them on the way back out is a header decision: a document upload normally wants
/// an <c>attachment</c> disposition carrying the user-facing file name, while a content-addressed asset
/// wants a long <c>Cache-Control</c>. Both are optional; <see cref="None"/> leaves the store's own
/// defaults in place.
/// </summary>
public sealed record FileUploadOptions
{
    private const string FallbackFileName = "file";

    /// <summary>The non-alphanumeric half of the RFC 5987 <c>attr-char</c> alphabet.</summary>
    private static ReadOnlySpan<byte> AttributePunctuation => "!#$&+-.^_`|~"u8;

    /// <summary>No headers: the store writes only the content type the caller passed.</summary>
    public static FileUploadOptions None { get; } = new();

    /// <summary>
    /// The RFC 6266 <c>Content-Disposition</c> header stored with the blob, for example
    /// <c>attachment; filename="deck.pptx"; filename*=UTF-8''deck.pptx</c>. <see langword="null"/>
    /// leaves the header unset.
    /// </summary>
    public string? ContentDisposition { get; init; }

    /// <summary>
    /// The <c>Cache-Control</c> header stored with the blob, for example
    /// <c>public, max-age=31536000, immutable</c>. <see langword="null"/> leaves the header unset.
    /// </summary>
    public string? CacheControl { get; init; }

    /// <summary>
    /// Builds an <c>attachment</c> disposition for a user-facing file name: the browser downloads the
    /// blob under that name instead of rendering it.
    /// </summary>
    /// <param name="fileName">
    /// The name to offer the user. Quotes, backslashes, semicolons and line breaks are dropped from the
    /// ASCII fallback and every non-ASCII character is replaced there with <c>_</c>; the exact name is
    /// preserved in the RFC 5987 <c>filename*</c> parameter as percent-encoded UTF-8. A blank name
    /// becomes <c>file</c>.
    /// </param>
    /// <param name="cacheControl">The <c>Cache-Control</c> value to store, or <see langword="null"/>.</param>
    /// <returns>Options carrying the built disposition and the given cache directive.</returns>
    public static FileUploadOptions Attachment(string fileName, string? cacheControl = null) =>
        ForDisposition("attachment", fileName, cacheControl);

    /// <summary>
    /// Builds an <c>inline</c> disposition for a user-facing file name: the browser renders the blob
    /// when it can (a PDF, for instance) and downloads it under that name when it cannot. Encoding
    /// rules are identical to <see cref="Attachment(string, string?)"/>.
    /// </summary>
    /// <param name="fileName">The name to offer the user. A blank name becomes <c>file</c>.</param>
    /// <param name="cacheControl">The <c>Cache-Control</c> value to store, or <see langword="null"/>.</param>
    /// <returns>Options carrying the built disposition and the given cache directive.</returns>
    public static FileUploadOptions Inline(string fileName, string? cacheControl = null) =>
        ForDisposition("inline", fileName, cacheControl);

    private static FileUploadOptions ForDisposition(string dispositionType, string fileName, string? cacheControl)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        string name = fileName.Trim();
        if (name.Length == 0)
        {
            name = FallbackFileName;
        }

        string fallback = ToAsciiFallback(name);
        string encoded = ToRfc5987(name);

        return new FileUploadOptions
        {
            ContentDisposition = string.Concat(
                dispositionType,
                "; filename=\"",
                fallback,
                "\"; filename*=UTF-8''",
                encoded),
            CacheControl = cacheControl,
        };
    }

    /// <summary>
    /// Reduces a name to the printable-ASCII quoted-string form: header-breaking characters are
    /// dropped outright and anything outside printable ASCII becomes <c>_</c>.
    /// </summary>
    private static string ToAsciiFallback(string fileName)
    {
        var builder = new StringBuilder(fileName.Length);
        foreach (char character in fileName)
        {
            if (character is '"' or '\\' or ';' or '\r' or '\n')
            {
                continue;
            }

            builder.Append(character is >= ' ' and <= '~' ? character : '_');
        }

        string fallback = builder.ToString().Trim();
        return fallback.Length == 0 ? FallbackFileName : fallback;
    }

    /// <summary>
    /// Percent-encodes the UTF-8 bytes of a name into the RFC 5987 <c>attr-char</c> alphabet, which is
    /// what the <c>filename*</c> parameter accepts.
    /// </summary>
    private static string ToRfc5987(string fileName)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(fileName);
        var builder = new StringBuilder(utf8.Length * 3);
        foreach (byte value in utf8)
        {
            if (IsAttributeCharacter(value))
            {
                builder.Append((char)value);
            }
            else
            {
                builder.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether the byte is an RFC 5987 <c>attr-char</c>, the alphabet a <c>filename*</c> value may use
    /// without percent-encoding.
    /// </summary>
    private static bool IsAttributeCharacter(byte value) =>
        value is >= (byte)'0' and <= (byte)'9'
            or >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'a' and <= (byte)'z'
        || AttributePunctuation.IndexOf(value) >= 0;
}
