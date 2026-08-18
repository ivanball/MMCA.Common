using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using System.Text;

namespace MMCA.Common.Shared.Abstractions;

/// <summary>
/// A request for one keyset ("seek") page: the rows strictly after a cursor, ordered by a single
/// sort key with the entity's <c>Id</c> as tie-break.
/// <para>
/// Keyset paging exists alongside the offset paging carried by <see cref="PaginationMetadata"/>,
/// it does not replace it. Offset paging answers "give me page 37" and costs the database a scan of
/// the 36 pages before it; keyset paging answers "give me what comes after this row" and costs one
/// index seek regardless of depth, at the price of losing random page access and a total count. Use
/// it for deep or infinite scrolling over large tables.
/// </para>
/// </summary>
[DataContract]
public sealed record KeysetPageRequest
{
    /// <summary>
    /// Framework ceiling on a keyset page, mirroring the query pipeline's own unbounded-result
    /// ceiling so neither entry point can be talked into an unbounded read.
    /// </summary>
    public const int MaxPageSize = 1000;

    /// <summary>Initializes an empty request (page size 1, no sort column, no cursor).</summary>
    public KeysetPageRequest()
        : this(pageSize: 1) { }

    /// <summary>Initializes a keyset page request.</summary>
    /// <param name="pageSize">
    /// The number of rows to return. Clamped into <c>[1, <see cref="MaxPageSize"/>]</c> rather than
    /// rejected, so a caller that asks for zero or a million gets a sane page instead of an error.
    /// </param>
    /// <param name="sortColumn">
    /// The single entity property to order by, or <see langword="null"/> to order by <c>Id</c> alone.
    /// </param>
    /// <param name="descending">Whether the sort key orders descending.</param>
    /// <param name="cursor">
    /// The opaque cursor returned as <see cref="KeysetCollectionResult{T}.NextCursor"/> by the
    /// previous page, or <see langword="null"/> for the first page.
    /// </param>
    public KeysetPageRequest(
        int pageSize,
        string? sortColumn = null,
        bool descending = false,
        string? cursor = null)
    {
        PageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        SortColumn = sortColumn;
        Descending = descending;
        Cursor = cursor;
    }

    /// <summary>Gets the number of rows to return, always within <c>[1, <see cref="MaxPageSize"/>]</c>.</summary>
    [DataMember(Order = 1)]
    public int PageSize
    {
        get;
        init => field = Math.Clamp(value, 1, MaxPageSize);
    }

    /// <summary>Gets the entity property to order by, or <see langword="null"/> to order by <c>Id</c> alone.</summary>
    [DataMember(Order = 2)]
    public string? SortColumn { get; init; }

    /// <summary>Gets a value indicating whether the sort key orders descending.</summary>
    [DataMember(Order = 3)]
    public bool Descending { get; init; }

    /// <summary>Gets the opaque cursor of the previous page, or <see langword="null"/> for the first page.</summary>
    [DataMember(Order = 4)]
    public string? Cursor { get; init; }
}

/// <summary>
/// A <see cref="CollectionResult{T}"/> for a keyset page: the rows plus the cursor that fetches the
/// next page. There is deliberately no total count and no page number, because a keyset read never
/// pays for either.
/// </summary>
/// <typeparam name="T">The type of items in the page.</typeparam>
[DataContract]
public sealed record KeysetCollectionResult<T> : CollectionResult<T>
{
    /// <summary>Initializes an empty keyset page with no next cursor.</summary>
    [SetsRequiredMembers]
    public KeysetCollectionResult()
        : this(items: [], nextCursor: null) { }

    /// <summary>Initializes a keyset page.</summary>
    /// <param name="items">The rows on this page.</param>
    /// <param name="nextCursor">
    /// The cursor that fetches the following page, or <see langword="null"/> when this page is the
    /// last one (there are no more rows).
    /// </param>
    [SetsRequiredMembers]
    public KeysetCollectionResult(IReadOnlyCollection<T> items, string? nextCursor)
        : base(items) => NextCursor = nextCursor;

    /// <summary>
    /// Gets the cursor that fetches the following page, or <see langword="null"/> when there are no
    /// more rows. Treat it as opaque: its encoding is an implementation detail and versioned.
    /// </summary>
    [DataMember(Order = 2)]
    public string? NextCursor { get; init; }
}

/// <summary>
/// Encodes and decodes the opaque cursor carried by <see cref="KeysetPageRequest.Cursor"/> and
/// <see cref="KeysetCollectionResult{T}.NextCursor"/>.
/// <para>
/// A cursor is the base64url form of <c>v1|{hasSortValue}|{sortValue}|{id}</c>, where the two value
/// segments are themselves base64url so a value containing the separator cannot forge one. The
/// <c>v1</c> prefix is a format version: a future encoding adds <c>v2</c> and
/// <see cref="TryDecode"/> keeps rejecting what it does not understand, instead of silently
/// mis-seeking.
/// </para>
/// <para>
/// It is not signed or encrypted, so it must never carry anything the caller may not see. The
/// values it does carry (a sort key and an id) are already in the rows the caller just received.
/// </para>
/// </summary>
public static class KeysetCursor
{
    private const string Version = "v1";
    private const char Separator = '|';

    /// <summary>
    /// Encodes a cursor from the last row of a page.
    /// </summary>
    /// <param name="sortValue">
    /// The row's sort-key value rendered with invariant culture, or <see langword="null"/> when the
    /// page is keyed by id alone (or the sort key was null on that row).
    /// </param>
    /// <param name="id">The row's identifier rendered with invariant culture.</param>
    /// <returns>The opaque cursor string.</returns>
    public static string Encode(string? sortValue, string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var payload = string.Concat(
            Version,
            Separator,
            sortValue is null ? "0" : "1",
            Separator,
            ToBase64Url(sortValue ?? string.Empty),
            Separator,
            ToBase64Url(id));

        return ToBase64Url(payload);
    }

    /// <summary>
    /// Decodes a cursor produced by <see cref="Encode"/>.
    /// </summary>
    /// <param name="cursor">The cursor string to decode.</param>
    /// <param name="sortValue">
    /// When this method returns <see langword="true"/>, the decoded sort-key value, or
    /// <see langword="null"/> when the cursor carries none.
    /// </param>
    /// <param name="id">When this method returns <see langword="true"/>, the decoded identifier.</param>
    /// <returns>
    /// <see langword="true"/> when the cursor is a well-formed <c>v1</c> cursor;
    /// <see langword="false"/> for anything else (wrong version, bad base64, wrong segment count).
    /// Callers turn <see langword="false"/> into a validation failure rather than a silent first page.
    /// </returns>
    public static bool TryDecode(string? cursor, out string? sortValue, out string id)
    {
        sortValue = null;
        id = string.Empty;

        if (string.IsNullOrWhiteSpace(cursor) || !TryFromBase64Url(cursor, out var payload))
            return false;

        var parts = payload.Split(Separator);
        if (parts.Length != 4 || !string.Equals(parts[0], Version, StringComparison.Ordinal))
            return false;

        if (parts[1] is not ("0" or "1"))
            return false;

        if (!TryFromBase64Url(parts[2], out var decodedSortValue) || !TryFromBase64Url(parts[3], out var decodedId))
            return false;

        sortValue = parts[1] == "1" ? decodedSortValue : null;
        id = decodedId;
        return true;
    }

    private static string ToBase64Url(string value) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private static bool TryFromBase64Url(string value, out string decoded)
    {
        decoded = string.Empty;

        if (value.Length == 0)
            return true;

        // The validity check comes first because TryDecodeFromChars THROWS on an invalid character
        // rather than returning false, and a client-supplied cursor is exactly the input that will
        // contain one.
        if (!Base64Url.IsValid(value))
            return false;

        var buffer = new byte[Base64Url.GetMaxDecodedLength(value.Length)];
        if (!Base64Url.TryDecodeFromChars(value, buffer, out var written))
            return false;

        decoded = Encoding.UTF8.GetString(buffer, 0, written);
        return true;
    }
}
