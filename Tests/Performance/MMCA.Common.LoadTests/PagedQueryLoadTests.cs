using AwesomeAssertions;
using MMCA.Common.LoadTests.Support;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.LoadTests;

/// <summary>
/// The framework's dynamic-query read path at volume: <c>EntityQueryService.GetAllAsync</c> (field
/// contract validation, dynamic filter, dynamic sort with the Id tie-break, COUNT plus Skip/Take, map
/// to DTO) over the real <c>UnitOfWork</c> read repository and <c>EFQueryableExecutor</c>, against
/// 100,000 seeded rows. Each shape asserts exact rows, not just a count, so a fast wrong answer fails.
/// </summary>
public sealed class PagedQueryLoadTests(PagedQueryFixture fixture) : IClassFixture<PagedQueryFixture>
{
    private const int PageSize = 25;

    // Measured runs per shape after one warm-up run (EF model build and query compilation are a
    // one-off cost per process, not a per-request one). The ceiling is checked against the MEDIAN.
    private const int MeasuredRuns = 5;

    // Median latency ceilings per shape (ms). Measured locally (i9-14900K, Windows, Release build,
    // 2026-10-01) as the three reference runs, then the worst of 25 runs; each ceiling is about 3x that worst.
    private const double FirstPageCeilingMs = 30; // 8.52 / 8.69 / 8.77 ms, worst 9.61
    private const double DeepPageCeilingMs = 45; // 13.36 / 14.19 / 13.87 ms, worst 14.86
    private const double FilteredSortedCeilingMs = 65; // 20.00 / 20.43 / 19.97 ms, worst 21.13

    private const int ConcurrentReads = 50;

    // p95 ceiling (ms) across 50 concurrent paged reads. Measured locally (same box and build):
    // 239.22 / 229.48 / 220.78 ms, worst 244.31 across 25 runs; ceiling about 3x the worst.
    private const double ConcurrentP95CeilingMs = 750;

    [Fact]
    public async Task PagedQueries_At100000Rows_ReturnExactRows_WithinLatencyCeilings()
    {
        var ct = TestContext.Current.CancellationToken;

        var firstPage = new PagedQuery("first-page", PageNumber: 1, PageSize);
        var deepPage = new PagedQuery("deep-page", PageNumber: 3_000, PageSize);
        var filteredSorted = new PagedQuery(
            "filtered-sorted-page",
            PageNumber: 2,
            PageSize,
            SortColumn: nameof(LoadItemDTO.Quantity),
            SortDirection: "desc",
            Filters: new Dictionary<string, (string Operator, string Value)>(StringComparer.Ordinal)
            {
                [nameof(LoadItemDTO.Category)] = ("EQUALS", LoadItem.CategoryOf(3)),
            });

        var first = await MeasureAsync(firstPage, ct);
        var deep = await MeasureAsync(deepPage, ct);
        var filtered = await MeasureAsync(filteredSorted, ct);

        await LoadResults.WriteAsync(
            "paged-queries",
            new
            {
                scenario = "paged-queries",
                rows = PagedQueryFixture.RowCount,
                seedSeconds = LoadResults.Round(fixture.SeedDuration.TotalSeconds),
                measuredRunsPerShape = MeasuredRuns,
                shapes = new[]
                {
                    Summary(first, FirstPageCeilingMs),
                    Summary(deep, DeepPageCeilingMs),
                    Summary(filtered, FilteredSortedCeilingMs),
                },
            },
            ct);

        // First page: default order is the Id tie-break, so rows 1..25 of all 100,000.
        first.Page.PaginationMetadata.TotalItemCount.Should().Be(PagedQueryFixture.RowCount);
        Ids(first.Page).Should().Equal(Enumerable.Range(1, PageSize));

        // Deep page 3,000: rows 74,976..75,000.
        deep.Page.PaginationMetadata.TotalItemCount.Should().Be(PagedQueryFixture.RowCount);
        Ids(deep.Page).Should().Equal(Enumerable.Range((3_000 - 1) * PageSize + 1, PageSize));

        // Category-3 is every Id ending in 3 (10,000 rows). Sorted by Quantity (Id % 1000) descending,
        // the top value is 993, held by the 100 Ids 993, 1993, ..., 99993; the Id tie-break orders them
        // ascending, so page 2 is the 26th..50th of those: 25993, 26993, ..., 49993.
        filtered.Page.PaginationMetadata.TotalItemCount.Should().Be(PagedQueryFixture.RowCount / LoadItem.CategoryCount);
        var filteredRows = Dtos(filtered.Page);
        filteredRows.Should().AllSatisfy(r =>
        {
            r.Category.Should().Be(LoadItem.CategoryOf(3));
            r.Quantity.Should().Be(993);
        });
        filteredRows.Select(r => r.Id).Should().Equal(Enumerable.Range(25, PageSize).Select(k => 993 + 1000 * k));

        first.MedianMs.Should().BeLessThanOrEqualTo(FirstPageCeilingMs, "first-page median latency ceiling");
        deep.MedianMs.Should().BeLessThanOrEqualTo(DeepPageCeilingMs, "deep-page median latency ceiling");
        filtered.MedianMs.Should().BeLessThanOrEqualTo(FilteredSortedCeilingMs, "filtered+sorted median latency ceiling");
    }

    [Fact]
    public async Task FiftyConcurrentPagedReads_AllSucceed_P95WithinCeiling()
    {
        var ct = TestContext.Current.CancellationToken;

        // A mix of the three shapes at varying pages, each in its own scope (its own request).
        var queries = Enumerable.Range(0, ConcurrentReads)
            .Select(i => (i % 3) switch
            {
                0 => new PagedQuery("first", PageNumber: 1 + i % 5, PageSize),
                1 => new PagedQuery("deep", PageNumber: 1_000 + i * 37, PageSize),
                _ => new PagedQuery(
                    "filtered",
                    PageNumber: 1 + i % 7,
                    PageSize,
                    SortColumn: nameof(LoadItemDTO.Quantity),
                    SortDirection: "desc",
                    Filters: new Dictionary<string, (string Operator, string Value)>(StringComparer.Ordinal)
                    {
                        [nameof(LoadItemDTO.Category)] = ("EQUALS", LoadItem.CategoryOf(i % LoadItem.CategoryCount)),
                    }),
            })
            .ToList();

        // Warm the process once (model build, query compilation) so the burst measures steady state.
        await fixture.RunAsync(queries[0], ct);
        await fixture.RunAsync(queries[1], ct);
        await fixture.RunAsync(queries[2], ct);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = queries
            .Select(q => Task.Run(
                async () =>
                {
                    await gate.Task;
                    return await fixture.RunAsync(q, ct);
                },
                ct))
            .ToList();

        var wall = System.Diagnostics.Stopwatch.StartNew();
        gate.SetResult();
        var outcomes = await Task.WhenAll(tasks);
        wall.Stop();

        var latencies = outcomes.Select(o => o.Milliseconds).ToList();
        var p50 = LoadResults.Percentile(latencies, 50);
        var p95 = LoadResults.Percentile(latencies, 95);

        await LoadResults.WriteAsync(
            "concurrent-paged-reads",
            new
            {
                scenario = "concurrent-paged-reads",
                rows = PagedQueryFixture.RowCount,
                concurrentReads = ConcurrentReads,
                wallMs = LoadResults.Round(wall.Elapsed.TotalMilliseconds),
                p50Ms = LoadResults.Round(p50),
                p95Ms = LoadResults.Round(p95),
                maxMs = LoadResults.Round(latencies.Max()),
                p95CeilingMs = ConcurrentP95CeilingMs,
            },
            ct);

        outcomes.Should().HaveCount(ConcurrentReads);
        outcomes.Should().AllSatisfy(o =>
        {
            o.Page.Items.Should().HaveCount(PageSize);
            o.Page.PaginationMetadata.TotalItemCount.Should().BeGreaterThan(0);
        });
        p95.Should().BeLessThanOrEqualTo(ConcurrentP95CeilingMs, "p95 latency ceiling under 50 concurrent reads");
    }

    private async Task<Measured> MeasureAsync(PagedQuery query, CancellationToken ct)
    {
        // Warm-up run, not measured.
        await fixture.RunAsync(query, ct);

        var samples = new List<double>(MeasuredRuns);
        PagedCollectionResult<object>? last = null;
        for (var run = 0; run < MeasuredRuns; run++)
        {
            var (page, ms) = await fixture.RunAsync(query, ct);
            samples.Add(ms);
            last = page;
        }

        return new Measured(query.Name, last!, LoadResults.Percentile(samples, 50), samples.Max());
    }

    private static object Summary(Measured measured, double ceilingMs) => new
    {
        shape = measured.Name,
        medianMs = LoadResults.Round(measured.MedianMs),
        maxMs = LoadResults.Round(measured.MaxMs),
        ceilingMs,
    };

    private static List<LoadItemDTO> Dtos(PagedCollectionResult<object> page) =>
        [.. page.Items.Cast<LoadItemDTO>()];

    private static List<int> Ids(PagedCollectionResult<object> page) =>
        [.. Dtos(page).Select(d => d.Id)];

    private sealed record Measured(string Name, PagedCollectionResult<object> Page, double MedianMs, double MaxMs);
}
