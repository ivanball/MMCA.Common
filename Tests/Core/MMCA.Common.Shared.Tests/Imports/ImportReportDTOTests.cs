using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Imports;
using MMCA.Common.Shared.Serialization;

namespace MMCA.Common.Shared.Tests.Imports;

/// <summary>
/// The shared import report: its wire shape (camelCase, <c>rowErrors</c> always an array, a derived
/// app-specific report keeping the base members), the row-error factory, and the dry-run field name.
/// </summary>
public sealed class ImportReportDTOTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RowErrorsDefaultsToEmpty()
    {
        var report = new ImportReportDTO { DryRun = true, TotalRows = 0 };

        report.RowErrors.Should().NotBeNull().And.BeEmpty();
        report.Added.Should().Be(0);
        report.Updated.Should().Be(0);
        report.Unchanged.Should().Be(0);
        report.Rejected.Should().Be(0);
    }

    [Fact]
    public void Serializes_CamelCaseMembers()
    {
        var report = new ImportReportDTO
        {
            DryRun = true,
            TotalRows = 7,
            Added = 1,
            Updated = 2,
            Unchanged = 3,
            Rejected = 1,
            RowErrors = [new ImportRowErrorDTO { LineNumber = 5, Column = "Email", Code = "Roster.EmailInvalid", Message = "Not an email." }],
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, WebOptions));
        JsonElement root = json.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "dryRun", "totalRows", "added", "updated", "unchanged", "rejected", "rowErrors");
        root.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        root.GetProperty("totalRows").GetInt32().Should().Be(7);
        root.GetProperty("rejected").GetInt32().Should().Be(1);

        JsonElement rowError = root.GetProperty("rowErrors").EnumerateArray().Should().ContainSingle().Subject;
        rowError.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "lineNumber", "column", "code", "message");
        rowError.GetProperty("lineNumber").GetInt32().Should().Be(5);
        rowError.GetProperty("column").GetString().Should().Be("Email");
        rowError.GetProperty("code").GetString().Should().Be("Roster.EmailInvalid");
    }

    [Fact]
    public void Serializes_RowErrorsAsAnArray_WhenThereAreNone()
    {
        var report = new ImportReportDTO { DryRun = false, TotalRows = 3, Added = 3 };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, WebOptions));

        json.RootElement.GetProperty("rowErrors").ValueKind.Should().Be(
            JsonValueKind.Array,
            "a client iterates rowErrors unconditionally, so it is [] rather than null or absent");
        json.RootElement.GetProperty("rowErrors").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void DerivedRecord_SerializesBaseAndOwnMembers()
    {
        var report = new RosterImportReportDTO
        {
            DryRun = false,
            TotalRows = 4,
            Added = 2,
            Voided = 1,
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, WebOptions));
        JsonElement root = json.RootElement;

        root.GetProperty("totalRows").GetInt32().Should().Be(4, "the derived report keeps the generic counts");
        root.GetProperty("added").GetInt32().Should().Be(2);
        root.GetProperty("dryRun").GetBoolean().Should().BeFalse();
        root.GetProperty("rowErrors").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("voided").GetInt32().Should().Be(1, "and adds its app-specific ones beside them");
    }

    [Fact]
    public void FromError_CopiesCodeMessageLineColumn()
    {
        var error = Error.Validation("Roster.EmailInvalid", "Not an email.", source: "RosterParser", target: "ignored");

        var rowError = ImportRowErrorDTO.FromError(error, lineNumber: 12, column: "Email");

        rowError.Code.Should().Be("Roster.EmailInvalid");
        rowError.Message.Should().Be("Not an email.");
        rowError.LineNumber.Should().Be(12);
        rowError.Column.Should().Be("Email");
    }

    [Fact]
    public void FromError_WithoutColumn_LeavesColumnNull()
    {
        var error = Error.Validation("Roster.RowEmpty", "The row has no values.");

        var rowError = ImportRowErrorDTO.FromError(error, lineNumber: 3);

        rowError.Column.Should().BeNull("a whole-row error names no column");
        rowError.LineNumber.Should().Be(3);
    }

    [Fact]
    public void FromError_Null_Throws()
    {
        Action act = () => ImportRowErrorDTO.FromError(null!, lineNumber: 1);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromError_LineNumber_IsTheCsvRowLineNumber()
    {
        var table = CsvReader.ReadTable("A,B\r\n\"multi\r\nline\",x\r\nbad,y\r\n").Value!;
        var badRow = table.Rows[1];

        var rowError = ImportRowErrorDTO.FromError(Error.Validation("Row.Bad", "Bad row."), badRow.LineNumber);

        rowError.LineNumber.Should().Be(
            4,
            "the line number is the 1-based file line the record starts on, so a quoted line break before it counts");
    }

    [Fact]
    public void DryRunFieldName_IsDryRun() =>
        ImportConventions.DryRunFieldName.Should().Be("dryRun");

    /// <summary>An app-specific report, standing in for ADC's roster import result.</summary>
    private sealed record RosterImportReportDTO : ImportReportDTO
    {
        public int Voided { get; init; }
    }
}
