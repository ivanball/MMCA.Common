using System.Text;
using AwesomeAssertions;
using MMCA.Common.API.Export;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.ValueObjects.Financial;

namespace MMCA.Common.API.Tests.Export;

/// <summary>
/// AS-15 / AC-21 (Store run 1, B3): a CSV export of a DTO carrying <see cref="Money"/>,
/// <see cref="Currency"/> or another nested record printed the C# record <c>ToString</c>
/// (<c>Money { Amount = 49.99, Currency = Currency { Code = USD } }</c>) into the cell. A reader
/// expects the value: Money as its amount followed by its currency code, Currency as its code, and
/// never the compiler-generated record text.
/// </summary>
public sealed class EntityCsvExporterValueObjectTests
{
    [Fact]
    public async Task MoneyCell_RendersAmountThenCurrencyCode()
    {
        var row = new PricedRow(1, Money.Create(49.99m, Currency.Usd).Value!, Currency.Eur);

        var cells = await ExportSingleRowAsync(row);

        cells["price"].Should().Be("49.99 USD");
    }

    [Fact]
    public async Task CurrencyCell_RendersTheIsoCode()
    {
        var row = new PricedRow(1, Money.Create(10m, Currency.Usd).Value!, Currency.Eur);

        var cells = await ExportSingleRowAsync(row);

        cells["currency"].Should().Be("EUR");
    }

    [Fact]
    public async Task NoCell_ContainsCompilerGeneratedRecordText()
    {
        var row = new DimensionedRow(7, Money.Create(49.99m, Currency.Usd).Value!, Currency.Usd, new ProbeDimensions(2, 3));

        var cells = await ExportSingleRowAsync(row);

        cells.Values.Should().AllSatisfy(cell =>
        {
            cell.Should().NotContain("{ ", "a record's ToString is debugging text, not a CSV value");
            cell.Should().NotContain(nameof(ProbeDimensions), "the type name of a nested record is never the value");
        });
    }

    private static async Task<Dictionary<string, string>> ExportSingleRowAsync<TRow>(TRow row)
        where TRow : class
    {
        await using var body = new MemoryStream();

        var result = await EntityCsvExporter<TRow>.WriteAsync(
            body,
            (pageNumber, _) => Task.FromResult(Result.Success(new PagedCollectionResult<object>(
                pageNumber == 1 ? [row] : [],
                new PaginationMetadata(1, 100, pageNumber)))),
            pageSize: 100,
            maxExportRows: 100_000,
            fields: null,
            beginResponse: () => { },
            onFailureAfterStart: (_, _) => { },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var lines = Encoding.UTF8.GetString(body.ToArray())
            .TrimStart('\uFEFF')
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2, "one header row and one data row");

        var columns = ParseRecord(lines[0]);
        var values = ParseRecord(lines[1]);
        values.Should().HaveCount(columns.Count, $"one cell per column (row: {lines[1]})");

        return columns.Zip(values).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
    }

    // Minimal RFC 4180 record parser: quoted fields, doubled quotes inside them, comma delimiter.
    private static List<string> ParseRecord(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var i = 0;

        while (i < line.Length)
        {
            var c = line[i++];
            if (quoted)
            {
                if (c == '"' && i < line.Length && line[i] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }

    public sealed record ProbeDimensions(int Width, int Height);

    public sealed record PricedRow(int Id, Money Price, Currency Currency);

    public sealed record DimensionedRow(int Id, Money Price, Currency Currency, ProbeDimensions Size);
}
