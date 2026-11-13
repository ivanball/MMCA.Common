using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.API.Uploads;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Serialization;

namespace MMCA.Common.API.Tests.Uploads;

/// <summary>
/// The upload-to-CSV helper: the bounded read's refusals first (with the caller's error), then the
/// parser's own errors unchanged, and on success the raw upload beside the parsed table.
/// </summary>
public sealed class FormFileCsvUploadTests
{
    private const long MaxBytes = 1024;

    private static readonly Error InvalidUpload = Error.Validation(
        code: "Ticket.Roster.FileRequired",
        message: "Upload one CSV file of at most 1 KB.",
        source: "TicketRosterImportController");

    // -- Rule 1: the bounded-read rules apply first, with the same arguments --
    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("oversize")]
    public async Task InvalidUpload_ReturnsCallerError(string shape)
    {
        IFormFile? file = shape switch
        {
            "null" => null,
            "empty" => FormFileUploadExtensionsTests.CreateFormFile([]),
            _ => FormFileUploadExtensionsTests.CreateFormFile(Encoding.UTF8.GetBytes(new string('a', (int)MaxBytes + 1))),
        };

        Result<CsvUpload> result = await file.ReadCsvAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Should().Be(
            InvalidUpload,
            "a file-level refusal is the caller's error, never a parser error");
    }

    [Fact]
    public async Task ArgumentChecks_ThrowAsTheBoundedReadDoes()
    {
        IFormFile file = FormFileUploadExtensionsTests.CreateFormFile(Encoding.UTF8.GetBytes("A\r\n1\r\n"));

        Func<Task> zeroCap = () => file.ReadCsvAsync(0, InvalidUpload, CancellationToken.None);
        Func<Task> nullError = () => file.ReadCsvAsync(MaxBytes, null!, CancellationToken.None);

        await zeroCap.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await nullError.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReadCsv_Cancelled_Throws()
    {
        IFormFile file = FormFileUploadExtensionsTests.CreateFormFile(Encoding.UTF8.GetBytes("A\r\n1\r\n"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => file.ReadCsvAsync(MaxBytes, InvalidUpload, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // -- Rule 2: the parser's errors, unchanged --
    [Fact]
    public async Task MalformedCsv_ReturnsCsvMalformed_WithLineTarget()
    {
        IFormFile file = FormFileUploadExtensionsTests.CreateFormFile(Encoding.UTF8.GetBytes("A,B\r\n1,\"never closed\r\n"));

        Result<CsvUpload> result = await file.ReadCsvAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        Error error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(CsvReader.MalformedCode);
        error.Type.Should().Be(ErrorType.Validation);
        error.Target.Should().Be("2", "the reader puts the malformed line in Target and the helper passes it on unchanged");
        error.Should().Be(
            CsvReader.ReadTable("A,B\r\n1,\"never closed\r\n").Errors.Single(),
            "the reader's error is returned exactly as the reader produced it");
    }

    [Fact]
    public async Task HeaderOnly_IsTableWithNoRows()
    {
        IFormFile file = FormFileUploadExtensionsTests.CreateFormFile(Encoding.UTF8.GetBytes("Name,Email\r\n"));

        Result<CsvUpload> result = await file.ReadCsvAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a header with no rows is a valid, empty import rather than a failure");
        result.Value!.Table.Headers.Should().Equal("Name", "Email");
        result.Value.Table.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task BlankText_ReturnsCsvEmpty()
    {
        IFormFile file = FormFileUploadExtensionsTests.CreateFormFile(Encoding.UTF8.GetBytes("\r\n\r\n"));

        Result<CsvUpload> result = await file.ReadCsvAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(CsvReader.EmptyCode);
    }

    // -- Rule 3: the raw upload beside the parsed table --
    [Fact]
    public async Task Success_CarriesBytesAndTable()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("Name,Email\r\nAda,ada@example.com\r\nGrace,grace@example.com\r\n");
        IFormFile file = FormFileUploadExtensionsTests.CreateFormFile(bytes, fileName: "roster.csv", contentType: "text/csv");

        Result<CsvUpload> result = await file.ReadCsvAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        CsvUpload upload = result.Value!;
        upload.File.Content.Should().Equal(bytes, "the raw bytes go to a command unchanged");
        upload.File.FileName.Should().Be("roster.csv");
        upload.File.ContentType.Should().Be("text/csv");
        upload.Table.Headers.Should().Equal("Name", "Email");
        upload.Table.Rows.Should().HaveCount(2);
        upload.Table.Rows[1].Fields.Should().Equal("Grace", "grace@example.com");
        upload.Table.Rows[0].LineNumber.Should().Be(2, "line numbers are file lines, header on line 1");
    }

    [Fact]
    public async Task Utf8BomFile_ParsesHeadersCleanly()
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Ticket ID,Barcode\r\n1,X\r\n")];
        IFormFile file = FormFileUploadExtensionsTests.CreateFormFile(bytes);

        Result<CsvUpload> result = await file.ReadCsvAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Table.Headers[0].Should().Be("Ticket ID", "the byte-order mark must not leak into the first header");
        result.Value.Table.FindColumn("ticket id").Should().Be(0);
        result.Value.File.Content.Should().Equal(bytes, "the stored upload keeps its original bytes, mark included");
    }
}
