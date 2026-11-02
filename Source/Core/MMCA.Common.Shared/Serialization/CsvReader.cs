using System.Globalization;
using System.Text;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Serialization;

/// <summary>
/// Reads comma-separated text per RFC 4180 into a <see cref="CsvTable"/>: the first record is the
/// header row, every later record a <see cref="CsvRow"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>A field in double quotes may hold commas, line breaks (CRLF, LF or CR) and doubled quotes
///     (<c>""</c> reads as one <c>"</c>).</item>
///   <item>Records end at CRLF, LF or a lone CR, so a file saved on any platform reads the same.</item>
///   <item>A leading byte-order mark is dropped, so a spreadsheet's "CSV UTF-8" export reads cleanly.</item>
///   <item>Blank lines are skipped; field values are returned exactly as written (not trimmed), while
///     header names are trimmed for lookup.</item>
/// </list>
/// A malformed file (a quote left open at the end, or text straight after a closing quote) is a
/// validation failure naming the line, never an exception, so an upload endpoint can answer 400 with it.
/// </remarks>
public static class CsvReader
{
    /// <summary>Code returned when the text is not well-formed CSV.</summary>
    public const string MalformedCode = "Csv.Malformed";

    /// <summary>Code returned when the text holds no header row.</summary>
    public const string EmptyCode = "Csv.Empty";

    private const char Quote = '"';
    private const char Comma = ',';

    /// <summary>Parses <paramref name="text"/> into a table whose first record is the header row.</summary>
    /// <param name="text">The CSV text.</param>
    /// <returns>The table, or a validation failure for malformed or empty text.</returns>
    public static Result<CsvTable> ReadTable(string text)
    {
        var parsed = ReadRecords(text);
        if (!parsed.TryGetValue(out var records))
        {
            return Result.Failure<CsvTable>(parsed.Errors);
        }

        if (records.Count == 0)
        {
            return Result.Failure<CsvTable>(Error.Validation(EmptyCode, "The file has no header row.", nameof(ReadTable)));
        }

        var headers = records[0].Fields.Select(h => h.Trim()).ToList();
        var rows = records.Skip(1).ToList();
        return Result.Success(new CsvTable(headers, rows));
    }

    /// <summary>
    /// Reads <paramref name="stream"/> to the end (honoring a byte-order mark, UTF-8 otherwise) and
    /// parses it as <see cref="ReadTable(string)"/> does. The caller bounds the stream's size.
    /// </summary>
    /// <param name="stream">The CSV content. Not disposed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The table, or a validation failure for malformed or empty text.</returns>
    public static async Task<Result<CsvTable>> ReadTableAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return ReadTable(text);
    }

    /// <summary>Parses <paramref name="text"/> into records, with no header interpretation.</summary>
    /// <param name="text">The CSV text.</param>
    /// <returns>Every non-blank record in order, or a validation failure for malformed text.</returns>
    public static Result<IReadOnlyList<CsvRow>> ReadRecords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var parser = new Parser(text);
        string? error = parser.Run();

        return error is null
            ? Result.Success<IReadOnlyList<CsvRow>>(parser.Records)
            : Result.Failure<IReadOnlyList<CsvRow>>(Error.Validation(MalformedCode, error, nameof(ReadRecords)));
    }

    /// <summary>One pass over the text; each member handles one token kind.</summary>
    private sealed class Parser(string text)
    {
        private readonly List<string> _fields = [];
        private readonly StringBuilder _field = new();
        private int _position = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        private int _line = 1;
        private int _recordLine = 1;
        private bool _recordHasContent;
        private bool _fieldHasContent;

        public List<CsvRow> Records { get; } = [];

        /// <summary>Parses the whole text.</summary>
        /// <returns>Null on success, otherwise the malformed-text message.</returns>
        public string? Run()
        {
            while (_position < text.Length)
            {
                char c = text[_position];
                string? error = c switch
                {
                    Quote when !_fieldHasContent => ReadQuotedField(),
                    Comma => EndField(),
                    '\r' or '\n' => EndLine(c),
                    _ => Append(c),
                };

                if (error is not null)
                {
                    return error;
                }
            }

            EndRecord();
            return null;
        }

        private string? Append(char c)
        {
            _field.Append(c);
            _fieldHasContent = true;
            _recordHasContent = true;
            _position++;
            return null;
        }

        private string? EndField()
        {
            _fields.Add(_field.ToString());
            _field.Clear();
            _fieldHasContent = false;
            _recordHasContent = true;
            _position++;
            return null;
        }

        private string? EndLine(char c)
        {
            EndRecord();
            _position += c == '\r' && IsAt(_position + 1, '\n') ? 2 : 1;
            _line++;
            _recordLine = _line;
            return null;
        }

        private string? ReadQuotedField()
        {
            int openedOn = _line;
            _position++;

            while (_position < text.Length)
            {
                char c = text[_position];
                if (c != Quote)
                {
                    CountLineBreak(c);
                    _field.Append(c);
                    _position++;
                }
                else if (IsAt(_position + 1, Quote))
                {
                    _field.Append(Quote);
                    _position += 2;
                }
                else
                {
                    _position++;
                    _fieldHasContent = true;
                    _recordHasContent = true;
                    return FollowsClosingQuote();
                }
            }

            return string.Create(CultureInfo.InvariantCulture, $"A quoted field opened on line {openedOn} is never closed.");
        }

        // After a closing quote only a separator or the end of the record may follow.
        private string? FollowsClosingQuote() =>
            _position >= text.Length || text[_position] is Comma or '\r' or '\n'
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"Unexpected text after a closing quote on line {_line}.");

        // A CRLF pair inside a quoted field is one line break, counted on its LF.
        private void CountLineBreak(char c)
        {
            if (c == '\n' || c == '\r' && !IsAt(_position + 1, '\n'))
            {
                _line++;
            }
        }

        private bool IsAt(int index, char expected) => index < text.Length && text[index] == expected;

        private void EndRecord()
        {
            // A blank line (nothing at all between two line breaks) is not a record.
            if (_recordHasContent)
            {
                _fields.Add(_field.ToString());
                Records.Add(new CsvRow(_recordLine, [.. _fields]));
            }

            _fields.Clear();
            _field.Clear();
            _recordHasContent = false;
            _fieldHasContent = false;
        }
    }
}

/// <summary>A parsed CSV file: its header names and its data rows.</summary>
/// <param name="headers">The header names, trimmed.</param>
/// <param name="rows">The data records after the header row.</param>
public sealed class CsvTable(IReadOnlyList<string> headers, IReadOnlyList<CsvRow> rows)
{
    /// <summary>Gets the header names, trimmed, in file order.</summary>
    public IReadOnlyList<string> Headers => headers;

    /// <summary>Gets the data rows, in file order.</summary>
    public IReadOnlyList<CsvRow> Rows => rows;

    /// <summary>
    /// Finds the column whose header equals one of <paramref name="names"/>, ignoring case and
    /// surrounding whitespace. The names are tried in order, so pass the preferred spelling first.
    /// </summary>
    /// <param name="names">The accepted header names, most preferred first.</param>
    /// <returns>The zero-based column index, or <see langword="null"/> when no header matches.</returns>
    public int? FindColumn(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        foreach (var name in names)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                if (string.Equals(headers[i], name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the first column whose header starts with <paramref name="prefix"/>, ignoring case, for
    /// exports that suffix a header with a varying label (a question's full text, say).
    /// </summary>
    /// <param name="prefix">The header prefix.</param>
    /// <returns>The zero-based column index, or <see langword="null"/> when no header matches.</returns>
    public int? FindColumnStartingWith(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        for (int i = 0; i < headers.Count; i++)
        {
            if (headers[i].StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }
}

/// <summary>One CSV record.</summary>
/// <param name="lineNumber">The 1-based line of the file the record starts on, for error messages.</param>
/// <param name="fields">The field values, exactly as written.</param>
public sealed class CsvRow(int lineNumber, IReadOnlyList<string> fields)
{
    /// <summary>Gets the 1-based line of the file the record starts on.</summary>
    public int LineNumber => lineNumber;

    /// <summary>Gets the field values, exactly as written.</summary>
    public IReadOnlyList<string> Fields => fields;

    /// <summary>Gets the value in <paramref name="column"/>, or an empty string past the end of a short record.</summary>
    /// <param name="column">The zero-based column index.</param>
    /// <returns>The value, or an empty string.</returns>
    public string this[int column] => column >= 0 && column < fields.Count ? fields[column] : string.Empty;

    /// <summary>
    /// Gets the value in <paramref name="column"/>, or an empty string when the column is unknown
    /// (<see langword="null"/>, as <see cref="CsvTable.FindColumn"/> returns for a missing header) or
    /// past the end of a short record.
    /// </summary>
    /// <param name="column">The zero-based column index, or <see langword="null"/>.</param>
    /// <returns>The value, or an empty string.</returns>
    public string Get(int? column) => column is { } index ? this[index] : string.Empty;
}
