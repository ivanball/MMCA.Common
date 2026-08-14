using System.Globalization;
using System.Text;
using AwesomeAssertions;
using MMCA.Common.API.Export;

namespace MMCA.Common.API.Tests.Export;

public sealed class CsvWriterTests
{
    private static string Write(Action<StringWriter> write)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        write(writer);
        return writer.ToString();
    }

    // ── RFC 4180 quoting ──
    [Fact]
    public void WriteRow_PlainValues_AreNotQuoted() =>
        Write(w => CsvWriter.WriteRow(["alpha", "beta"], w))
            .Should().Be("alpha,beta\r\n", because: "RFC 4180 quotes only fields that need it");

    [Fact]
    public void WriteRow_ValueWithComma_IsQuoted() =>
        Write(w => CsvWriter.WriteRow(["a,b", "c"], w))
            .Should().Be("\"a,b\",c\r\n", because: "an embedded delimiter would otherwise split the record");

    [Fact]
    public void WriteRow_ValueWithQuote_IsQuotedAndDoubled() =>
        Write(w => CsvWriter.WriteRow(["say \"hi\"", "x"], w))
            .Should().Be("\"say \"\"hi\"\"\",x\r\n", because: "RFC 4180 escapes a quote by doubling it");

    [Fact]
    public void WriteRow_ValueWithLineFeed_IsQuoted() =>
        Write(w => CsvWriter.WriteRow(["line1\nline2"], w))
            .Should().Be("\"line1\nline2\"\r\n", because: "an embedded LF must not terminate the record");

    [Fact]
    public void WriteRow_ValueWithCarriageReturn_IsQuoted() =>
        Write(w => CsvWriter.WriteRow(["line1\rline2"], w))
            .Should().Be("\"line1\rline2\"\r\n", because: "an embedded CR must not terminate the record");

    [Fact]
    public void WriteRow_ValueWithCrLf_IsQuoted() =>
        Write(w => CsvWriter.WriteRow(["line1\r\nline2"], w))
            .Should().Be("\"line1\r\nline2\"\r\n", because: "an embedded record separator must be protected");

    [Fact]
    public void WriteRow_EmptyCells_ProduceBareDelimiters() =>
        Write(w => CsvWriter.WriteRow([string.Empty, string.Empty], w))
            .Should().Be(",\r\n", because: "an empty field needs no quoting");

    [Fact]
    public void WriteRow_NoCells_WritesOnlyTheLineEnding() =>
        Write(w => CsvWriter.WriteRow([], w))
            .Should().Be("\r\n", because: "a record with no fields is still a record");

    // ── Line endings ──
    [Fact]
    public void WriteRow_AlwaysTerminatesWithCrLf() =>
        Write(w =>
        {
            CsvWriter.WriteRow(["a"], w);
            CsvWriter.WriteRow(["b"], w);
        })
            .Should().Be("a\r\nb\r\n", because: "RFC 4180 records are CRLF-terminated on every platform");

    [Fact]
    public void LineEnding_IsCrLf() =>
        CsvWriter.LineEnding.Should().Be("\r\n");

    // ── Header row ──
    [Fact]
    public void WriteHeader_EscapesColumnNamesLikeDataFields() =>
        Write(w => CsvWriter.WriteHeader(["id", "full,name"], w))
            .Should().Be("id,\"full,name\"\r\n", because: "a header field follows the same quoting rules");

    // ── Byte order mark ──
    [Fact]
    public void WriteByteOrderMark_WritesExactlyOneMark()
    {
        var output = Write(w =>
        {
            CsvWriter.WriteByteOrderMark(w);
            CsvWriter.WriteHeader(["id"], w);
            CsvWriter.WriteRow([1], w);
        });

        output.Should().StartWith("\uFEFF", because: "Excel needs the BOM to read the file as UTF-8");
        output.Count(c => c == '\uFEFF').Should().Be(1, because: "a second BOM would show up as a stray column");
    }

    [Fact]
    public void Utf8NoPreamble_EmitsNoPreambleOfItsOwn()
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, CsvWriter.Utf8NoPreamble, leaveOpen: true))
        {
            CsvWriter.WriteByteOrderMark(writer);
            CsvWriter.WriteHeader(["id"], writer);
        }

        byte[] bytes = stream.ToArray();

        Convert.ToHexString(bytes.Take(3).ToArray()).Should().Be("EFBBBF", because: "the explicit BOM is the first thing on the wire");
        Encoding.UTF8.GetString(bytes).Count(c => c == '\uFEFF')
            .Should().Be(1, because: "the encoding must not add a preamble on top of the explicit mark");
    }

    // ── Cell formatting ──
    [Fact]
    public void FormatCell_Null_IsEmpty() =>
        CsvWriter.FormatCell(null).Should().BeEmpty();

    [Fact]
    public void FormatCell_String_IsVerbatim() =>
        CsvWriter.FormatCell(" padded ").Should().Be(" padded ", because: "strings are written as given");

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void FormatCell_Bool_IsLowercase(bool value, string expected) =>
        CsvWriter.FormatCell(value).Should().Be(expected, because: "the JSON endpoints emit lowercase booleans");

    [Fact]
    public void FormatCell_DateTime_IsIso8601RoundTrip()
    {
        var value = new DateTime(2026, 8, 13, 14, 30, 15, 250, DateTimeKind.Utc);

        CsvWriter.FormatCell(value).Should().Be("2026-08-13T14:30:15.2500000Z");
    }

    [Fact]
    public void FormatCell_DateTimeOffset_IsIso8601RoundTrip()
    {
        var value = new DateTimeOffset(2026, 8, 13, 14, 30, 15, TimeSpan.FromHours(-4));

        CsvWriter.FormatCell(value).Should().Be("2026-08-13T14:30:15.0000000-04:00");
    }

    [Fact]
    public void FormatCell_Decimal_UsesInvariantCultureRegardlessOfAmbientCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // de-DE writes 1234,56 and would turn one cell into two.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            CsvWriter.FormatCell(1234.56m).Should().Be("1234.56");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void FormatCell_DateTime_UsesInvariantCultureRegardlessOfAmbientCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            CsvWriter.FormatCell(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))
                .Should().Be("2026-01-02T03:04:05.0000000Z");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void FormatCell_Guid_UsesInvariantFormatting() =>
        CsvWriter.FormatCell(Guid.Empty).Should().Be("00000000-0000-0000-0000-000000000000");

    [Fact]
    public void WriteRow_MixedCells_FormatsEachByType() =>
        Write(w => CsvWriter.WriteRow([1, null, true, "x,y"], w))
            .Should().Be("1,,true,\"x,y\"\r\n");

    // ── Guard clauses ──
    [Fact]
    public void WriteRow_NullWriter_Throws() =>
        FluentActions.Invoking(() => CsvWriter.WriteRow(["a"], writer: null!))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void WriteHeader_NullColumns_Throws() =>
        FluentActions.Invoking(() => CsvWriter.WriteHeader(null!, new StringWriter(CultureInfo.InvariantCulture)))
            .Should().Throw<ArgumentNullException>();
}
