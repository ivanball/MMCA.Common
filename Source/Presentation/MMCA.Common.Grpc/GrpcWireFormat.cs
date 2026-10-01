using System.Globalization;

namespace MMCA.Common.Grpc;

/// <summary>
/// The string encodings hand-written gRPC services and their client adapters use for values protobuf
/// has no lossless scalar for: UTC timestamps as ISO 8601 round-trip (<c>"O"</c>) strings and money as
/// invariant-culture decimal strings. Both ends of a contract call these so the format is written
/// once.
/// <para>
/// <b>Timestamps.</b> SQL Server hands <see cref="DateTime"/> columns back as
/// <see cref="DateTimeKind.Unspecified"/>, and <c>"O"</c> omits the <c>Z</c> marker for that kind, so
/// <see cref="FormatUtc"/> stamps <see cref="DateTimeKind.Utc"/> before formatting. The stored values
/// are already UTC; this only restores the marker the wire contract promises (a value of another kind
/// is relabelled, never converted). <see cref="ParseUtc"/> reads either form, with or without the
/// marker, as <see cref="DateTimeKind.Utc"/> on the same instant, because a rolling deploy can put a
/// replica that still emits the marker-less form on the other end.
/// </para>
/// <para>
/// <b>Money.</b> A double would round a price; the invariant culture on both ends keeps
/// <c>"1234.50"</c> from being read back as twelve hundred thousand under a comma-decimal locale.
/// </para>
/// <para>
/// <b>Bad input.</b> <see cref="ParseUtc"/> and <see cref="ParseDecimal"/> throw
/// <see cref="FormatException"/> on a malformed value, treating a corrupt peer payload as a fault.
/// An adapter that maps malformed input to its own answer instead (for example <see langword="null"/>)
/// uses <see cref="TryParseDecimal"/> and decides itself.
/// </para>
/// </summary>
public static class GrpcWireFormat
{
    // AssumeUniversal + AdjustToUniversal: a suffix-less value would otherwise parse as
    // Kind=Unspecified, and AssumeUniversal alone yields Kind=Local. A Z-suffixed value takes the same
    // path and keeps its instant. RoundtripKind is deliberately absent: DateTime.Parse rejects it
    // alongside either Assume* or AdjustToUniversal (ArgumentException), and its job (preserving a
    // non-UTC kind) is the opposite of what this contract wants.
    private const DateTimeStyles UtcStyles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>Formats a UTC timestamp as an ISO 8601 round-trip string ending in <c>Z</c>.</summary>
    /// <param name="value">The timestamp, stored as UTC whatever its <see cref="DateTime.Kind"/> says.</param>
    /// <returns>The <c>"O"</c>-formatted string, for example <c>2026-05-03T09:00:00.0000000Z</c>.</returns>
    public static string FormatUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats an optional UTC timestamp for a contract that carries "absent" as the empty string
    /// (proto3 has no null string). Pair with <see cref="ParseUtcOrNull"/>.
    /// </summary>
    /// <param name="value">The timestamp, or <see langword="null"/>.</param>
    /// <returns>The empty string for <see langword="null"/>; otherwise <see cref="FormatUtc"/>.</returns>
    public static string FormatUtcOrEmpty(DateTime? value) =>
        value is null ? string.Empty : FormatUtc(value.Value);

    /// <summary>
    /// Parses an ISO 8601 timestamp, with or without the <c>Z</c> marker, as
    /// <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    /// <param name="value">The wire string.</param>
    /// <returns>The UTC timestamp.</returns>
    /// <exception cref="FormatException"><paramref name="value"/> is not a valid timestamp.</exception>
    public static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, UtcStyles);

    /// <summary>
    /// Parses an optional timestamp written by <see cref="FormatUtcOrEmpty"/>: the empty string
    /// (also what a peer predating the field sends) is <see langword="null"/>.
    /// </summary>
    /// <param name="value">The wire string.</param>
    /// <returns><see langword="null"/> for an empty value; otherwise <see cref="ParseUtc"/>.</returns>
    /// <exception cref="FormatException">A non-empty <paramref name="value"/> is not a valid timestamp.</exception>
    public static DateTime? ParseUtcOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : ParseUtc(value);

    /// <summary>Formats a decimal as an invariant-culture string, keeping its scale.</summary>
    /// <param name="value">The amount.</param>
    /// <returns>The invariant string, for example <c>1234.50</c>.</returns>
    public static string FormatDecimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses an invariant-culture decimal string (<see cref="NumberStyles.Number"/>).</summary>
    /// <param name="value">The wire string.</param>
    /// <returns>The amount.</returns>
    /// <exception cref="FormatException"><paramref name="value"/> is not a valid decimal.</exception>
    public static decimal ParseDecimal(string value) =>
        decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses an invariant-culture decimal string (<see cref="NumberStyles.Number"/>) without
    /// throwing, for an adapter that maps a malformed amount to its own answer.
    /// </summary>
    /// <param name="value">The wire string.</param>
    /// <param name="result">The amount, or zero when parsing fails.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> parsed.</returns>
    public static bool TryParseDecimal(string? value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
}
