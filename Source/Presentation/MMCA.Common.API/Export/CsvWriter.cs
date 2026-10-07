using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MMCA.Common.Shared.ValueObjects.Financial;

namespace MMCA.Common.API.Export;

/// <summary>
/// Minimal RFC 4180 CSV writer used by the generic <c>/export</c> endpoint. It writes into a
/// caller-supplied <see cref="TextWriter"/> one row at a time. <c>EntityCsvExporter</c> points that
/// writer at a per-page <see cref="MemoryStream"/> and drains it to the response body with awaited
/// writes after each page, so an export holds at most one page of rendered CSV in memory, never the
/// full result set.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately hand-written rather than taking a CsvHelper dependency: the framework needs exactly
/// three behaviours (quote when required, escape embedded quotes, terminate with CRLF), and every
/// package this repo ships is a dependency every consumer inherits.
/// </para>
/// <para>
/// Value formatting is fixed and invariant, so the same row produces the same bytes on every machine:
/// <see langword="null"/> writes an empty field; <see langword="string"/> writes verbatim;
/// <see langword="bool"/> writes lowercase to match the JSON the sibling endpoints emit, not the
/// capitalized form .NET's own ToString produces; <see cref="DateTime"/> and
/// <see cref="DateTimeOffset"/> write ISO 8601 round-trip ("O"), chosen over the sortable "s" format
/// because "O" keeps sub-second precision and the offset, so a parsed value equals the one exported;
/// anything else <see cref="IFormattable"/> formats with <see cref="CultureInfo.InvariantCulture"/>;
/// <see cref="Money"/> writes its amount and currency code ("49.99 USD") and <see cref="Currency"/>
/// its code; any other value whose <c>ToString</c> is the compiler-generated record text or the
/// inherited type name writes as compact JSON.
/// </para>
/// <para>
/// The writer does NOT prefix fields that a spreadsheet would evaluate as formulas (values opening
/// with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>). CSV is a data-faithful format here and mangling a
/// value that begins with a minus sign would corrupt legitimate negative numbers; a host that opens
/// untrusted exports in a spreadsheet should import them as text.
/// </para>
/// </remarks>
internal static class CsvWriter
{
    /// <summary>
    /// The UTF-8 byte order mark, written as the first characters of an export file.
    /// </summary>
    /// <remarks>
    /// Excel reads a BOM-less UTF-8 CSV in the machine's ANSI code page, which turns every accented
    /// or non-Latin character into mojibake on the desktops these exports are opened on. The BOM
    /// costs three bytes and is ignored by every other consumer worth supporting, so it is written
    /// unconditionally rather than offered as a setting nobody would find in time.
    /// </remarks>
    internal const string Utf8ByteOrderMark = "\uFEFF";

    /// <summary>The RFC 4180 record separator: CRLF, regardless of the host operating system.</summary>
    internal const string LineEnding = "\r\n";

    /// <summary>
    /// UTF-8 with no encoder preamble, for the <see cref="StreamWriter"/> an export streams through.
    /// The byte order mark is written explicitly by <see cref="WriteByteOrderMark"/> instead, so the
    /// file gets exactly one and the decision lives in a single place.
    /// </summary>
    internal static readonly Encoding Utf8NoPreamble = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Characters that force a field to be quoted per RFC 4180 section 2: the delimiter itself, the
    /// quote character, and either half of a line break.
    /// </summary>
    private static readonly SearchValues<char> MustQuote = SearchValues.Create(",\"\r\n");

    /// <summary>Per-type answer of <see cref="HasNoValueText"/>, so reflection runs once per type.</summary>
    private static readonly ConcurrentDictionary<Type, bool> NoValueTextTypes = new();

    /// <summary>
    /// Writes the UTF-8 byte order mark. Call this once, before the header row, on an export written
    /// through a writer whose encoding does not emit a preamble of its own (otherwise the file gets
    /// two).
    /// </summary>
    /// <param name="writer">The destination writer.</param>
    internal static void WriteByteOrderMark(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Write(Utf8ByteOrderMark);
    }

    /// <summary>
    /// Writes the header record: one field per column name, escaped exactly like a data field.
    /// </summary>
    /// <param name="columns">The column names, in output order.</param>
    /// <param name="writer">The destination writer.</param>
    internal static void WriteHeader(IReadOnlyList<string> columns, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(writer);

        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            WriteField(columns[i] ?? string.Empty, writer);
        }

        writer.Write(LineEnding);
    }

    /// <summary>
    /// Writes one data record. Cell values are formatted per the rules documented on
    /// <see cref="CsvWriter"/> and quoted only when RFC 4180 requires it.
    /// </summary>
    /// <param name="cells">The cell values, in the same order as the header columns.</param>
    /// <param name="writer">The destination writer.</param>
    internal static void WriteRow(IReadOnlyList<object?> cells, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(writer);

        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            WriteField(FormatCell(cells[i]), writer);
        }

        writer.Write(LineEnding);
    }

    /// <summary>
    /// Converts a cell value to its invariant text form. See the type remarks for the full table.
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The text written to the field (before any quoting).</returns>
    internal static string FormatCell(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        bool flag => flag ? "true" : "false",
        DateTime timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
        _ => FormatObject(value)
    };

    /// <summary>
    /// Formats a cell value that is neither a primitive nor <see cref="IFormattable"/>: the money
    /// value objects get their value form, a type whose <c>ToString</c> is debugging text writes as
    /// compact JSON, and anything else writes its own invariant text.
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The cell text.</returns>
    private static string FormatObject(object value) => value switch
    {
        Money money => FormatMoney(money),
        Currency currency => currency.Code,
        _ when HasNoValueText(value.GetType()) => JsonSerializer.Serialize(value, value.GetType(), JsonSerializerOptions.Web),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    /// <summary>
    /// Formats a <see cref="Money"/> cell as its invariant amount followed by its ISO 4217 code
    /// ("49.99 USD"); a currency-less zero writes the amount alone.
    /// </summary>
    /// <param name="money">The money value.</param>
    /// <returns>The cell text.</returns>
    private static string FormatMoney(Money money)
    {
        var amount = money.Amount.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(money.Currency.Code) ? amount : $"{amount} {money.Currency.Code}";
    }

    /// <summary>
    /// Decides whether a type's <c>ToString</c> is debugging text rather than a value: the
    /// compiler-generated record form (<c>Name { Member = ... }</c>) or the inherited
    /// <see cref="object.ToString"/>, which prints the type name. Such a cell is written as compact
    /// JSON instead, the same shape the sibling JSON endpoints return. Cached per type.
    /// </summary>
    /// <param name="type">The runtime type of the cell value.</param>
    /// <returns><see langword="true"/> when the type has no meaningful text of its own.</returns>
    private static bool HasNoValueText(Type type) =>
        NoValueTextTypes.GetOrAdd(type, static t =>
        {
            var toString = t.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            return toString is null
                || toString.DeclaringType == typeof(object)
                || toString.DeclaringType == typeof(ValueType)
                || toString.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);
        });

    /// <summary>
    /// Writes a single already-formatted field, quoting it and doubling any embedded quote when the
    /// text contains a comma, a quote, or a line break.
    /// </summary>
    /// <param name="field">The formatted field text.</param>
    /// <param name="writer">The destination writer.</param>
    private static void WriteField(string field, TextWriter writer)
    {
        if (field.AsSpan().IndexOfAny(MustQuote) < 0)
        {
            writer.Write(field);
            return;
        }

        writer.Write('"');
        writer.Write(field.Replace("\"", "\"\"", StringComparison.Ordinal));
        writer.Write('"');
    }
}
