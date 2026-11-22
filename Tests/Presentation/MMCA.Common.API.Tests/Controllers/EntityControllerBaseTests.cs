using System.Dynamic;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.API.Tests.Controllers;

public sealed class EntityControllerBaseTests
{
    private readonly Mock<IEntityQueryService<TestEntity, TestDTO, int>> _queryServiceMock = new();
    private readonly Mock<ILogger<EntityControllerBase<TestEntity, TestDTO, int>>> _loggerMock = new();

    private TestEntityController CreateController(int? maxPageSize = null)
    {
        var services = new ServiceCollection();
        if (maxPageSize.HasValue)
        {
            services.AddSingleton(Options.Create(new ApplicationSettings { MaxPageSize = maxPageSize.Value }));
        }

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        return new TestEntityController(_queryServiceMock.Object, _loggerMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
    }

    private void SetupGetAllPagedSuccess(PagedCollectionResult<object> items) =>
        _queryServiceMock
            .Setup(q => q.GetAllAsync(
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<Specification<TestEntity, int>?>(),
                It.IsAny<Dictionary<string, (string, string)>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(items));

    private void SetupGetAllPagedFailure(Error error) =>
        _queryServiceMock
            .Setup(q => q.GetAllAsync(
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<Specification<TestEntity, int>?>(),
                It.IsAny<Dictionary<string, (string, string)>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<PagedCollectionResult<object>>(error));

    // ── GetAllAsync (non-paged) ──
    [Fact]
    public async Task GetAllAsync_Success_ReturnsOkWithResult()
    {
        var items = new PagedCollectionResult<object>(
            items: [],
            paginationMetadata: new PaginationMetadata(0, 500, 1));
        SetupGetAllPagedSuccess(items);
        TestEntityController sut = CreateController(maxPageSize: 500);

        ActionResult<CollectionResult<TestDTO>> result =
            await sut.GetAllAsync(fields: null, includeFKs: false, includeChildren: false, cancellationToken: CancellationToken.None);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value.Should().Be(items);
    }

    [Fact]
    public async Task GetAllAsync_Failure_ReturnsHandleFailure()
    {
        SetupGetAllPagedFailure(Error.Failure("Test.Failure", "Something went wrong"));
        TestEntityController sut = CreateController(maxPageSize: 500);

        ActionResult<CollectionResult<TestDTO>> result =
            await sut.GetAllAsync(fields: null, includeFKs: false, includeChildren: false, cancellationToken: CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── GetAllAsync (paged) ──
    [Fact]
    public async Task GetAllPaged_Success_ReturnsOkWithPaginationHeader()
    {
        var metadata = new PaginationMetadata(totalItemCount: 50, pageSize: 10, currentPage: 1);
        var items = new PagedCollectionResult<object>(items: [], paginationMetadata: metadata);
        SetupGetAllPagedSuccess(items);
        TestEntityController sut = CreateController(maxPageSize: 100);

        ActionResult<PagedCollectionResult<TestDTO>> result = await sut.GetAllAsync(
            pageNumber: 1,
            pageSize: 10,
            cancellationToken: CancellationToken.None);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        sut.Response.Headers["X-Pagination"].ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetAllPaged_Failure_ReturnsHandleFailure()
    {
        SetupGetAllPagedFailure(Error.NotFoundError("Test.NotFound", "Not found"));
        TestEntityController sut = CreateController(maxPageSize: 100);

        ActionResult<PagedCollectionResult<TestDTO>> result = await sut.GetAllAsync(
            pageNumber: 1,
            pageSize: 10,
            cancellationToken: CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    [Fact]
    public async Task GetAllPaged_PageSizeExceedsMax_ClampedToMax()
    {
        const int maxPageSize = 25;
        var metadata = new PaginationMetadata(totalItemCount: 100, pageSize: maxPageSize, currentPage: 1);
        var items = new PagedCollectionResult<object>(items: [], paginationMetadata: metadata);
        _queryServiceMock
            .Setup(q => q.GetAllAsync(
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<Specification<TestEntity, int>?>(),
                It.IsAny<Dictionary<string, (string, string)>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                maxPageSize,
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(items));
        TestEntityController sut = CreateController(maxPageSize: maxPageSize);

        await sut.GetAllAsync(pageNumber: 1, pageSize: 9999, cancellationToken: CancellationToken.None);

        _queryServiceMock.Verify(q => q.GetAllAsync(
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<Specification<TestEntity, int>?>(),
            It.IsAny<Dictionary<string, (string, string)>?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            maxPageSize,
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── GetAllForLookupAsync ──
    [Fact]
    public async Task GetAllForLookupAsync_Success_ReturnsOkWithLookups()
    {
        IReadOnlyCollection<BaseLookup<int>> lookups =
        [
            new BaseLookup<int> { Id = 1, Name = "Item One" },
            new BaseLookup<int> { Id = 2, Name = "Item Two" }
        ];
        _queryServiceMock
            .Setup(q => q.GetAllForLookupAsync(
                "Name",
                null,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(lookups));
        TestEntityController sut = CreateController(maxPageSize: 100);

        ActionResult<CollectionResult<BaseLookup<int>>> result =
            await sut.GetAllForLookupAsync("Name", CancellationToken.None);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        var collectionResult = okResult.Value as CollectionResult<BaseLookup<int>>;
        collectionResult.Should().NotBeNull();
        collectionResult!.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllForLookupAsync_Failure_ReturnsHandleFailure()
    {
        _queryServiceMock
            .Setup(q => q.GetAllForLookupAsync(
                "Name",
                null,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyCollection<BaseLookup<int>>>(
                Error.Failure("Test.Failure", "Lookup failed")));
        TestEntityController sut = CreateController(maxPageSize: 100);

        ActionResult<CollectionResult<BaseLookup<int>>> result =
            await sut.GetAllForLookupAsync("Name", CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── GetByIdAsync ──
    [Fact]
    public async Task GetByIdAsync_Success_ReturnsOk()
    {
        var expando = new ExpandoObject();
        ((IDictionary<string, object?>)expando)["Id"] = 42;
        _queryServiceMock
            .Setup(q => q.GetByIdAsync(
                42,
                true,
                false,
                It.IsAny<Specification<TestEntity, int>?>(),
                It.IsAny<string?>(),
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<object>(expando));
        TestEntityController sut = CreateController(maxPageSize: 100);

        ActionResult<TestDTO> result = await sut.GetByIdAsync(42, cancellationToken: CancellationToken.None);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value.Should().Be(expando);
    }

    [Fact]
    public async Task GetByIdAsync_Failure_ReturnsHandleFailure()
    {
        _queryServiceMock
            .Setup(q => q.GetByIdAsync(
                99,
                true,
                false,
                It.IsAny<Specification<TestEntity, int>?>(),
                It.IsAny<string?>(),
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<object>(
                Error.NotFoundError("Test.NotFound", "Entity not found")));
        TestEntityController sut = CreateController(maxPageSize: 100);

        ActionResult<TestDTO> result = await sut.GetByIdAsync(99, cancellationToken: CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── HandleFailure ──
    [Fact]
    public void HandleFailure_LogsWarning()
    {
        _loggerMock.Setup(l => l.IsEnabled(LogLevel.Warning)).Returns(true);
        TestEntityController sut = CreateController(maxPageSize: 100);
        Error[] errors = [Error.Validation("Test.Code", "Test message")];

        sut.InvokeHandleFailure(errors);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // B5 (D-03): a failure message carrying CR or LF is logged on one line, with the code and both
    // halves of the message still present.
    [Theory]
    [InlineData("first line\r\nforged: second line")]
    [InlineData("first line\nforged: second line")]
    [InlineData("first line\rforged: second line")]
    public void HandleFailure_AMessageWithALineBreak_IsLoggedOnOneLine(string message)
    {
        var logger = new LineRecordingLogger();
        var sut = new TestEntityController(_queryServiceMock.Object, logger)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        sut.InvokeHandleFailure([Error.Validation("Test.Code", message)]);

        var line = logger.Lines.Should().ContainSingle().Which;
        line.Should().NotContain("\r").And.NotContain("\n");
        line.Should().Contain("Test.Code").And.Contain("first line").And.Contain("forged: second line");
    }

    // ── MaxPageSize ──
    [Fact]
    public void MaxPageSize_UsesSettingsValue()
    {
        TestEntityController sut = CreateController(maxPageSize: 42);

        int maxPageSize = sut.ExposedMaxPageSize;

        maxPageSize.Should().Be(42);
    }

    [Fact]
    public void MaxPageSize_FallsBackTo500_WhenSettingsNull()
    {
        TestEntityController sut = CreateController(maxPageSize: null);

        int maxPageSize = sut.ExposedMaxPageSize;

        maxPageSize.Should().Be(500);
    }

    /// <summary>Keeps every Warning-or-above line as the sink would render it.</summary>
    private sealed class LineRecordingLogger : ILogger<EntityControllerBase<TestEntity, TestDTO, int>>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines => _lines;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }
}

public sealed class TestEntityController(
    IEntityQueryService<TestEntity, TestDTO, int> queryService,
    ILogger<EntityControllerBase<TestEntity, TestDTO, int>> logger)
    : EntityControllerBase<TestEntity, TestDTO, int>(queryService, logger)
{
    public int ExposedMaxPageSize => MaxPageSize;

    public ObjectResult InvokeHandleFailure(IEnumerable<Error> errors) =>
        HandleFailure(errors);
}

public sealed class TestEntity : AuditableBaseEntity<int>;

public sealed record TestDTO : IBaseDTO<int>
{
    public required int Id { get; init; }
}
