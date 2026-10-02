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

    // Kestrel forbids synchronous I/O on the response body by default. An export whose CSV is larger
    // than the writer's buffer (about 1 KB) must still arrive complete, so nothing may ever reach the
    // body through a synchronous Write or Flush.
    [Fact]
    public async Task WriteAsync_WhenTheBodyForbidsSynchronousIo_StreamsAnExportLargerThanOneKilobyteCompletely()
    {
        await using var body = new AsyncOnlyResponseStream();
        var name = new string('x', 100);

        var result = await EntityCsvExporter<ExportRow>.WriteAsync(
            body,
            (pageNumber, _) =>
            {
                var items = pageNumber <= 3
                    ? Enumerable.Range((pageNumber - 1) * 100 + 1, 100).Select(i => (object)new ExportRow(i, name)).ToList()
                    : [];
                return Task.FromResult(Result.Success(
                    new PagedCollectionResult<object>(items, new PaginationMetadata(300, 100, pageNumber))));
            },
            pageSize: 100,
            maxExportRows: 100_000,
            fields: null,
            beginResponse: () => { },
            onFailureAfterStart: (_, _) => { },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var bytes = body.ToArray();
        bytes.Length.Should().BeGreaterThan(30_000, "the export is far larger than the writer's 1 KB buffer");
        var lines = Encoding.UTF8.GetString(bytes)
            .TrimStart('﻿')
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1 + 300);
        lines[^1].Should().Be($"300,{name}");
    }

    [Fact]
    public async Task WriteAsync_WhenTheBodyForbidsSynchronousIo_StillWritesTheIncompleteMarkerAfterALaterPageFails()
    {
        await using var body = new AsyncOnlyResponseStream();
        var name = new string('y', 100);

        var result = await EntityCsvExporter<ExportRow>.WriteAsync(
            body,
            (pageNumber, _) => Task.FromResult(pageNumber == 1
                ? Result.Success(new PagedCollectionResult<object>(
                    [.. Enumerable.Range(1, 50).Select(i => (object)new ExportRow(i, name))],
                    new PaginationMetadata(100, 50, 1)))
                : Result.Failure<PagedCollectionResult<object>>(Error.Failure("Export.Down", "The store is unavailable."))),
            pageSize: 50,
            maxExportRows: 100_000,
            fields: null,
            beginResponse: () => { },
            onFailureAfterStart: (_, _) => { },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var lines = Encoding.UTF8.GetString(body.ToArray())
            .TrimStart('﻿')
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1 + 50 + 1, "the header, the 50 rows already written, and the incomplete marker");
    }

    public sealed record ExportRow(int Id, string Name);
}
