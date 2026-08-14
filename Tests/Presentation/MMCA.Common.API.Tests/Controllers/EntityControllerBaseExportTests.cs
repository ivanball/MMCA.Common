using System.Dynamic;
using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using Moq;
using Moq.Language;

namespace MMCA.Common.API.Tests.Controllers;

public sealed class EntityControllerBaseExportTests : IDisposable
{
    private static readonly DateTimeOffset ExportInstant = new(2026, 8, 13, 10, 15, 0, TimeSpan.Zero);

    private readonly Mock<IEntityQueryService<ExportTestEntity, ExportTestDTO, int>> _queryServiceMock = new();
    private readonly Mock<ILogger<EntityControllerBase<ExportTestEntity, ExportTestDTO, int>>> _loggerMock = new();
    private readonly MemoryStream _body = new();

    private ExportTestController CreateController(int maxPageSize = 500, int maxExportRows = 100_000)
    {
        var settingsMock = new Mock<IApplicationSettings>();
        settingsMock.Setup(s => s.MaxPageSize).Returns(maxPageSize);
        settingsMock.Setup(s => s.MaxExportRows).Returns(maxExportRows);

        var services = new ServiceCollection();
        services.AddSingleton(settingsMock.Object);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(ExportInstant));
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };
        httpContext.Response.Body = _body;

        return new ExportTestController(_queryServiceMock.Object, _loggerMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
    }

    public void Dispose() => _body.Dispose();

    private string BodyText() => Encoding.UTF8.GetString(_body.ToArray());

    private static string[] BodyLines(string body) =>
        body.TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    private static PagedCollectionResult<object> Page(IReadOnlyCollection<object> items, int totalItemCount) =>
        new(items, new PaginationMetadata(totalItemCount, pageSize: items.Count == 0 ? 1 : items.Count, currentPage: 1));

    private static ExportTestDTO Dto(int id, string? name = null, bool isActive = true) =>
        new() { Id = id, Name = name ?? string.Create(CultureInfo.InvariantCulture, $"Name {id}"), IsActive = isActive, CreatedOn = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

    private static ExpandoObject Shaped(params (string Key, object? Value)[] pairs)
    {
        IDictionary<string, object?> expando = new ExpandoObject();
        foreach ((string key, object? value) in pairs)
        {
            expando[key] = value;
        }

        return (ExpandoObject)expando;
    }

    private ISetupSequentialResult<Task<Result<PagedCollectionResult<object>>>> SetupPages() =>
        _queryServiceMock.SetupSequence(q => q.GetAllAsync(
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<Specification<ExportTestEntity, int>?>(),
            It.IsAny<Dictionary<string, (string, string)>?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()));

    // ── Page-loop fan-in ──
    [Fact]
    public async Task ExportAsync_ThreePages_FansInEveryRow()
    {
        SetupPages()
            .ReturnsAsync(Result.Success(Page([Dto(1), Dto(2)], totalItemCount: 5)))
            .ReturnsAsync(Result.Success(Page([Dto(3), Dto(4)], totalItemCount: 5)))
            .ReturnsAsync(Result.Success(Page([Dto(5)], totalItemCount: 5)));
        ExportTestController sut = CreateController(maxPageSize: 2);

        IActionResult result = await sut.ExportAsync(cancellationToken: CancellationToken.None);

        result.Should().BeOfType<EmptyResult>(because: "the CSV was written straight to the response body");
        string[] lines = BodyLines(BodyText());
        lines.Should().HaveCount(6, because: "one header row plus five data rows across three pages");
        lines[1].Should().StartWith("1,Name 1");
        lines[5].Should().StartWith("5,Name 5");
    }

    [Fact]
    public async Task ExportAsync_ShortFirstPage_StopsAfterOneQuery()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([Dto(1)], totalItemCount: 1)));
        ExportTestController sut = CreateController(maxPageSize: 10);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        _queryServiceMock.Verify(
            q => q.GetAllAsync(
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<Specification<ExportTestEntity, int>?>(),
                It.IsAny<Dictionary<string, (string, string)>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a page shorter than the page size is the last page");
    }

    [Fact]
    public async Task ExportAsync_PagesThroughIncrementingPageNumbers()
    {
        SetupPages()
            .ReturnsAsync(Result.Success(Page([Dto(1)], totalItemCount: 2)))
            .ReturnsAsync(Result.Success(Page([Dto(2)], totalItemCount: 2)))
            .ReturnsAsync(Result.Success(Page([], totalItemCount: 2)));
        ExportTestController sut = CreateController(maxPageSize: 1);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        foreach (int pageNumber in new[] { 1, 2, 3 })
        {
            _queryServiceMock.Verify(
                q => q.GetAllAsync(
                    It.IsAny<bool>(),
                    It.IsAny<bool>(),
                    It.IsAny<Specification<ExportTestEntity, int>?>(),
                    It.IsAny<Dictionary<string, (string, string)>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    pageNumber,
                    1,
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    // ── Truncation ──
    [Fact]
    public async Task ExportAsync_AlwaysAdvertisesTheRowLimit()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([Dto(1)], totalItemCount: 1)));
        ExportTestController sut = CreateController(maxPageSize: 10, maxExportRows: 7);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        sut.Response.Headers[EntityControllerBase<ExportTestEntity, ExportTestDTO, int>.ExportRowLimitHeaderName]
            .ToString().Should().Be("7", because: "the ceiling is knowable before the body starts, so it rides in a header");
    }

    [Fact]
    public async Task ExportAsync_CapReachedMidPage_AppendsTruncationMarker()
    {
        SetupPages()
            .ReturnsAsync(Result.Success(Page([Dto(1), Dto(2)], totalItemCount: 10)))
            .ReturnsAsync(Result.Success(Page([Dto(3), Dto(4)], totalItemCount: 10)));
        ExportTestController sut = CreateController(maxPageSize: 2, maxExportRows: 3);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        string[] lines = BodyLines(BodyText());
        lines.Should().HaveCount(5, because: "header plus three data rows plus the truncation marker");
        lines[^1].Should().Be("# export truncated at 3 rows");
        sut.Response.Headers[EntityControllerBase<ExportTestEntity, ExportTestDTO, int>.ExportRowLimitHeaderName]
            .ToString().Should().Be("3");
    }

    [Fact]
    public async Task ExportAsync_CapOnPageBoundaryWithMoreRows_AppendsTruncationMarker()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([Dto(1), Dto(2)], totalItemCount: 9)));
        ExportTestController sut = CreateController(maxPageSize: 2, maxExportRows: 2);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        BodyLines(BodyText())[^1].Should().Be(
            "# export truncated at 2 rows",
            because: "the total says rows were left behind even though the cap fell on a page boundary");
    }

    [Fact]
    public async Task ExportAsync_CapOnPageBoundaryWithNoMoreRows_WritesNoMarker()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([Dto(1), Dto(2)], totalItemCount: 2)));
        ExportTestController sut = CreateController(maxPageSize: 2, maxExportRows: 2);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        string body = BodyText();
        body.Should().NotContain("# export truncated", because: "the export is complete, it just happens to end at the cap");
        BodyLines(body).Should().HaveCount(3);
    }

    [Fact]
    public async Task ExportAsync_WithinTheCap_WritesNoMarker()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([Dto(1)], totalItemCount: 1)));
        ExportTestController sut = CreateController(maxPageSize: 10, maxExportRows: 100);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        BodyText().Should().NotContain("# export truncated");
    }

    // ── Column resolution ──
    [Fact]
    public async Task ExportAsync_NoFields_UsesCamelCaseDtoColumns()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([Dto(1, "Ada")], totalItemCount: 1)));
        ExportTestController sut = CreateController(maxPageSize: 10);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        string[] lines = BodyLines(BodyText());
        lines[0].Should().Be("id,name,isActive,createdOn", because: "CSV columns must match the JSON property names");
        lines[1].Should().Be("1,Ada,true,2026-01-01T00:00:00.0000000Z");
    }

    [Fact]
    public async Task ExportAsync_WithFields_UsesShapedRowKeys()
    {
        SetupPages().ReturnsAsync(Result.Success(Page(
            [Shaped(("id", 1), ("name", "Ada")), Shaped(("id", 2), ("name", "Grace"))],
            totalItemCount: 2)));
        ExportTestController sut = CreateController(maxPageSize: 10);

        await sut.ExportAsync(fields: "Id,Name", cancellationToken: CancellationToken.None);

        string[] lines = BodyLines(BodyText());
        lines[0].Should().Be("id,name", because: "the query service already shaped the rows to the requested fields");
        lines[1].Should().Be("1,Ada");
        lines[2].Should().Be("2,Grace");
    }

    [Fact]
    public async Task ExportAsync_EmptyResult_WritesHeaderOnly()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([], totalItemCount: 0)));
        ExportTestController sut = CreateController(maxPageSize: 10);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        string[] lines = BodyLines(BodyText());
        lines.Should().ContainSingle(because: "an empty export still names its columns");
        lines[0].Should().Be("id,name,isActive,createdOn");
    }

    [Fact]
    public async Task ExportAsync_EmptyResultWithFields_HeaderIsTheRequestedSubset()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([], totalItemCount: 0)));
        ExportTestController sut = CreateController(maxPageSize: 10);

        await sut.ExportAsync(fields: "Name,Id", cancellationToken: CancellationToken.None);

        BodyLines(BodyText())[0].Should().Be(
            "id,name",
            because: "with no row to read keys from, the columns come from the DTO filtered to the requested fields, in DTO order");
    }

    // ── Escaping through the endpoint ──
    [Fact]
    public async Task ExportAsync_ValuesNeedingEscape_AreQuoted()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([Dto(1, "Doe, Jane")], totalItemCount: 1)));
        ExportTestController sut = CreateController(maxPageSize: 10);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        BodyText().Should().Contain("1,\"Doe, Jane\",true,");
    }

    [Fact]
    public async Task ExportAsync_WritesExactlyOneByteOrderMark()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([Dto(1)], totalItemCount: 1)));
        ExportTestController sut = CreateController(maxPageSize: 10);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        byte[] bytes = _body.ToArray();
        Convert.ToHexString(bytes.Take(3).ToArray()).Should().Be("EFBBBF");
        BodyText().Count(c => c == '\uFEFF').Should().Be(1);
    }

    // ── Response metadata ──
    [Fact]
    public async Task ExportAsync_SetsCsvContentTypeAndAttachmentFileName()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([Dto(1)], totalItemCount: 1)));
        ExportTestController sut = CreateController(maxPageSize: 10);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        sut.Response.ContentType.Should().Be("text/csv; charset=utf-8");
        sut.Response.Headers.ContentDisposition.ToString().Should().Be(
            "attachment; filename=\"ExportTest-20260813T101500Z.csv\"",
            because: "the file name is the controller stem plus an invariant UTC timestamp from the time provider");
    }

    // ── Failures ──
    [Fact]
    public async Task ExportAsync_FirstPageFailure_ReturnsProblemDetailsAndWritesNothing()
    {
        SetupPages().ReturnsAsync(Result.Failure<PagedCollectionResult<object>>(
            Error.NotFoundError("Test.NotFound", "Not found")));
        ExportTestController sut = CreateController(maxPageSize: 10);

        IActionResult result = await sut.ExportAsync(cancellationToken: CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
        _body.ToArray().Should().BeEmpty(because: "nothing was flushed, so the status line was still ours to set");
    }

    [Fact]
    public async Task ExportAsync_LaterPageFailure_AppendsIncompleteMarker()
    {
        SetupPages()
            .ReturnsAsync(Result.Success(Page([Dto(1), Dto(2)], totalItemCount: 10)))
            .ReturnsAsync(Result.Failure<PagedCollectionResult<object>>(
                Error.Failure("Test.Failure", "Query blew up")));
        ExportTestController sut = CreateController(maxPageSize: 2);

        IActionResult result = await sut.ExportAsync(cancellationToken: CancellationToken.None);

        result.Should().BeOfType<EmptyResult>(because: "the headers were already sent, so Problem Details is no longer reachable");
        BodyLines(BodyText())[^1].Should().Be("# export incomplete after 2 rows");
    }

    // ── Query surface pass-through ──
    [Fact]
    public async Task ExportAsync_PassesFiltersSortAndIncludesToTheQueryService()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([Dto(1)], totalItemCount: 1)));
        ExportTestController sut = CreateController(maxPageSize: 25);
        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = ("contains", "ada")
        };

        await sut.ExportAsync(
            includeFKs: true,
            sortColumn: "Name",
            sortDirection: "desc",
            fields: null,
            filters: filters,
            cancellationToken: CancellationToken.None);

        _queryServiceMock.Verify(
            q => q.GetAllAsync(
                true,
                false,
                null,
                filters,
                "Name",
                "desc",
                null,
                1,
                25,
                false,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the export must query exactly what the paged endpoint would, at the max page size");
    }

    // ── Settings resolution ──
    [Fact]
    public async Task ExportAsync_NonPositiveConfiguredCap_FallsBackToTheDefault()
    {
        SetupPages().ReturnsAsync(Result.Success(Page([Dto(1)], totalItemCount: 1)));
        ExportTestController sut = CreateController(maxPageSize: 10, maxExportRows: 0);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        sut.Response.Headers[EntityControllerBase<ExportTestEntity, ExportTestDTO, int>.ExportRowLimitHeaderName]
            .ToString().Should().Be("100000", because: "a cap of zero would serve every caller a header-only file");
        BodyLines(BodyText()).Should().HaveCount(2);
    }
}

public sealed class ExportTestController(
    IEntityQueryService<ExportTestEntity, ExportTestDTO, int> queryService,
    ILogger<EntityControllerBase<ExportTestEntity, ExportTestDTO, int>> logger)
    : EntityControllerBase<ExportTestEntity, ExportTestDTO, int>(queryService, logger);

public sealed class ExportTestEntity : AuditableBaseEntity<int>;

public sealed record ExportTestDTO : IBaseDTO<int>
{
    public required int Id { get; init; }

    public string? Name { get; init; }

    public bool IsActive { get; init; }

    public DateTime CreatedOn { get; init; }
}
