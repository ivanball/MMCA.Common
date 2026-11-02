using System.Text;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Serialization;

namespace MMCA.Common.Shared.Tests.Serialization;

/// <summary>
/// RFC 4180 reading: quoted fields with commas, doubled quotes and embedded line breaks, every line
/// ending, a byte-order mark, blank lines, short records, header lookup by name or prefix ignoring
/// case, and malformed text reported as a validation failure rather than an exception.
/// </summary>
public sealed class CsvReaderTests
{
    [Fact]
    public void ReadTable_SplitsTheHeaderFromTheRows()
    {
        var table = Read("Name,Email\r\nAda,ada@example.com\r\nGrace,grace@example.com\r\n");

        table.Headers.Should().Equal("Name", "Email");
        table.Rows.Should().HaveCount(2);
        table.Rows[1].Fields.Should().Equal("Grace", "grace@example.com");
    }

    [Fact]
    public void QuotedFields_KeepCommasDoubledQuotesAndLineBreaks()
    {
        var table = Read("A,B,C\r\n\"Lovelace, Ada\",\"She said \"\"hi\"\"\",\"line one\r\nline two\"\r\n");

        table.Rows.Should().ContainSingle();
        table.Rows[0].Fields.Should().Equal("Lovelace, Ada", "She said \"hi\"", "line one\r\nline two");
    }

    [Fact]
    public void AQuotedFieldAfterAComma_IsRecognized()
    {
        var table = Read("A,B\nplain,\"quoted, with comma\"\n");

        table.Rows[0].Fields.Should().Equal("plain", "quoted, with comma");
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void EveryLineEnding_EndsARecord(string newline)
    {
        var table = Read($"A,B{newline}1,2{newline}3,4");

        table.Rows.Should().HaveCount(2);
        table.Rows[0].Fields.Should().Equal("1", "2");
        table.Rows[1].Fields.Should().Equal("3", "4");
    }

    [Fact]
    public void ALeadingByteOrderMark_IsDropped()
    {
        var table = Read("﻿Ticket ID,Barcode\r\n1,X\r\n");

        table.Headers[0].Should().Be("Ticket ID");
        table.FindColumn("ticket id").Should().Be(0);
    }

    [Fact]
    public void BlankLines_AreSkipped_AndEmptyFieldsKept()
    {
        var table = Read("A,B,C\r\n\r\n1,,3\r\n\r\n,,\r\n");

        table.Rows.Should().HaveCount(2);
        table.Rows[0].Fields.Should().Equal("1", string.Empty, "3");
        table.Rows[1].Fields.Should().Equal(string.Empty, string.Empty, string.Empty);
    }

    [Fact]
    public void FieldValues_AreNotTrimmed_ButHeadersAre()
    {
        var table = Read(" Name , Email \r\n Ada , x \r\n");

        table.Headers.Should().Equal("Name", "Email");
        table.Rows[0].Fields.Should().Equal(" Ada ", " x ");
    }

    [Fact]
    public void FindColumn_TriesTheNamesInOrder_IgnoringCase()
    {
        var table = Read("Buyer Email,(Order) Email Address\r\nb@x.com,o@x.com\r\n");

        table.FindColumn("(order) email address", "Buyer Email").Should().Be(1);
        table.FindColumn("Missing", "BUYER EMAIL").Should().Be(0);
        table.FindColumn("Missing").Should().BeNull();
    }

    [Fact]
    public void FindColumnStartingWith_MatchesAHeaderByItsPrefix()
    {
        var table = Read("Ticket ID,Attendee First Name (as on badge)\r\n1,Ada\r\n");

        table.FindColumnStartingWith("attendee first name").Should().Be(1);
        table.FindColumnStartingWith("Nope").Should().BeNull();
    }

    [Fact]
    public void TheRowIndexer_AnswersEmptyForAMissingColumnOrAShortRecord()
    {
        var table = Read("A,B,C\r\n1\r\n");
        var row = table.Rows[0];

        row.Get(table.FindColumn("C")).Should().BeEmpty("a short record has no third field");
        row.Get(table.FindColumn("Missing")).Should().BeEmpty("an unknown column reads as empty");
        row[0].Should().Be("1");
    }

    [Fact]
    public void Rows_CarryTheLineTheyStartOn()
    {
        var table = Read("A,B\r\n\"multi\r\nline\",x\r\nnext,y\r\n");

        table.Rows[0].LineNumber.Should().Be(2);
        table.Rows[1].LineNumber.Should().Be(4, "the quoted line break moved the second record down a line");
    }

    [Fact]
    public void AnUnclosedQuote_IsAValidationFailureNamingTheLine()
    {
        Result<CsvTable> result = CsvReader.ReadTable("A,B\r\n1,\"never closed\r\n");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == CsvReader.MalformedCode && e.Type == ErrorType.Validation && e.Message.Contains("line 2", StringComparison.Ordinal));
    }

    [Fact]
    public void TextAfterAClosingQuote_IsAValidationFailure()
    {
        Result<CsvTable> result = CsvReader.ReadTable("A\r\n\"x\"y\r\n");

        result.Errors.Should().ContainSingle(e => e.Code == CsvReader.MalformedCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n\r\n")]
    public void EmptyText_IsAValidationFailure(string text)
    {
        Result<CsvTable> result = CsvReader.ReadTable(text);

        result.Errors.Should().ContainSingle(e => e.Code == CsvReader.EmptyCode);
    }

    [Fact]
    public async Task ReadTableAsync_HonorsTheByteOrderMarkAndLeavesTheStreamOpen()
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Nombre\r\nJosé\r\n")];
        await using var stream = new MemoryStream(bytes);

        Result<CsvTable> result = await CsvReader.ReadTableAsync(stream);

        result.Value!.Headers.Should().Equal("Nombre");
        result.Value.Rows[0][0].Should().Be("José");
        stream.CanRead.Should().BeTrue("the caller owns the stream");
    }

    private static CsvTable Read(string text)
    {
        Result<CsvTable> result = CsvReader.ReadTable(text);
        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Value!;
    }
}
