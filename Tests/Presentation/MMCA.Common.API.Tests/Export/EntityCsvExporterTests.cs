using System.Text;
using AwesomeAssertions;
using MMCA.Common.API.Export;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.API.Tests.Export;

/// <summary>
/// The CSV export loop decides end-of-data from the data (M120). The query pipeline clamps every page
/// to its own ceiling, so a full clamped page can be shorter than the page size the caller asked for;
/// treating it as the last page would silently truncate the export with no marker line.
/// </summary>
public sealed class EntityCsvExporterTests
{
    [Fact]
    public async Task WriteAsync_WhenAFullPageIsShorterThanTheRequestedSize_KeepsFetchingUntilTheTotal()
    {
        var requestedPages = new List<int>();
        await using var body = new MemoryStream();

        var result = await EntityCsvExporter<ExportRow>.WriteAsync(
            body,
            (pageNumber, _) =>
            {
                requestedPages.Add(pageNumber);
                var count = pageNumber switch
                {
                    1 => 1000,
                    2 => 500,
                    _ => 0,
                };
                var firstId = pageNumber == 1 ? 1 : 1001;
                var items = Enumerable.Range(firstId, count)
                    .Select(i => (object)new ExportRow(i, "row"))
                    .ToList();
                return Task.FromResult(Result.Success(
                    new PagedCollectionResult<object>(items, new PaginationMetadata(1500, 1000, pageNumber))));
            },
            pageSize: 5000,
            maxExportRows: 100_000,
            fields: null,
            beginResponse: () => { },
            onFailureAfterStart: (_, _) => { },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        requestedPages.Should().Equal(1, 2);

        var lines = Encoding.UTF8.GetString(body.ToArray())
            .TrimStart('\uFEFF')
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1 + 1500, "one header row and every one of the 1500 data rows, with no marker line");
    }

    public sealed record ExportRow(int Id, string Name);
}
