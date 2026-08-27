using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.API.Concurrency;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.API.Tests.Controllers;

/// <summary>
/// The read-scoping hook: every read action (both list overloads, lookup, by-id and export) asks
/// <c>GetReadSpecificationAsync</c> for the rows this caller may see, and a controller that
/// overrides nothing queries exactly as unscoped as it always did.
/// </summary>
public sealed class EntityControllerBaseReadSpecificationTests : IDisposable
{
    private static readonly DateTimeOffset ExportInstant = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly Mock<ILogger<EntityControllerBase<ReadScopeEntity, ReadScopeDTO, int>>> _loggerMock = new();
    private readonly MemoryStream _body = new();

    public void Dispose() => _body.Dispose();

    private ControllerContext CreateControllerContext(int maxPageSize)
    {
        var settingsMock = new Mock<IApplicationSettings>();
        settingsMock.Setup(s => s.MaxPageSize).Returns(maxPageSize);
        settingsMock.Setup(s => s.MaxExportRows).Returns(100);

        var services = new ServiceCollection();
        services.AddSingleton(settingsMock.Object);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(ExportInstant));

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        httpContext.Response.Body = _body;

        return new ControllerContext { HttpContext = httpContext };
    }

    private TController Create<TController>(
        Func<IEntityQueryService<ReadScopeEntity, ReadScopeDTO, int>,
            ILogger<EntityControllerBase<ReadScopeEntity, ReadScopeDTO, int>>, TController> factory,
        RecordingQueryService queryService,
        int maxPageSize = 50)
        where TController : ControllerBase
    {
        TController controller = factory(queryService, _loggerMock.Object);
        controller.ControllerContext = CreateControllerContext(maxPageSize);
        return controller;
    }

    private static Specification<ReadScopeEntity, int> OddRows() =>
        new InlineSpecification<ReadScopeEntity, int>(e => e.Id % 2 == 1);

    // ── Default hook: today's behaviour, unchanged ──
    [Fact]
    public async Task GetAllAsync_WithNoOverride_QueriesUnscoped()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        UnscopedReadController sut = Create(
            (q, l) => new UnscopedReadController(q, l), queryService);

        await sut.GetAllAsync(fields: null, includeFKs: false, includeChildren: false, cancellationToken: CancellationToken.None);

        queryService.SpecificationsSeen.Should().ContainSingle().Which.Should().BeNull(
            because: "a controller that overrides neither hook must query exactly as it always did");
    }

    [Fact]
    public async Task GetAllPaged_WithNoOverride_QueriesUnscoped()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        UnscopedReadController sut = Create(
            (q, l) => new UnscopedReadController(q, l), queryService);

        await sut.GetAllAsync(pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

        queryService.SpecificationsSeen.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task GetAllForLookupAsync_WithNoOverride_PassesNoPredicate()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        UnscopedReadController sut = Create(
            (q, l) => new UnscopedReadController(q, l), queryService);

        await sut.GetAllForLookupAsync("Name", CancellationToken.None);

        queryService.LookupPredicatesSeen.Should().ContainSingle().Which.Should().BeNull(
            because: "a null specification means the lookup query stays exactly as unfiltered as before");
    }

    [Fact]
    public async Task GetByIdAsync_WithNoOverride_QueriesUnscoped()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        UnscopedReadController sut = Create(
            (q, l) => new UnscopedReadController(q, l), queryService);

        ActionResult<ReadScopeDTO> result = await sut.GetByIdAsync(2, cancellationToken: CancellationToken.None);

        queryService.SpecificationsSeen.Should().ContainSingle().Which.Should().BeNull();
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ExportAsync_WithNoOverride_QueriesUnscoped()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        UnscopedReadController sut = Create(
            (q, l) => new UnscopedReadController(q, l), queryService, maxPageSize: 10);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        queryService.SpecificationsSeen.Should().AllSatisfy(seen => seen.Should().BeNull());
        BodyLines().Should().HaveCount(4, because: "a header row plus all three unscoped rows");
    }

    // ── Overridden async hook: the scope reaches all five actions ──
    [Fact]
    public async Task GetAllAsync_WithTheAsyncHookOverridden_PassesTheSpecification()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        Specification<ReadScopeEntity, int> specification = OddRows();
        AsyncScopedReadController sut = Create(
            (q, l) => new AsyncScopedReadController(q, l, specification), queryService);

        await sut.GetAllAsync(fields: null, includeFKs: false, includeChildren: false, cancellationToken: CancellationToken.None);

        queryService.SpecificationsSeen.Should().ContainSingle().Which.Should().BeSameAs(specification);
    }

    [Fact]
    public async Task GetAllPaged_WithTheAsyncHookOverridden_PassesTheSpecification()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        Specification<ReadScopeEntity, int> specification = OddRows();
        AsyncScopedReadController sut = Create(
            (q, l) => new AsyncScopedReadController(q, l, specification), queryService);

        ActionResult<PagedCollectionResult<ReadScopeDTO>> result =
            await sut.GetAllAsync(pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

        queryService.SpecificationsSeen.Should().ContainSingle().Which.Should().BeSameAs(specification);
        var page = (result.Result as OkObjectResult)!.Value as PagedCollectionResult<object>;
        page!.Items.Should().HaveCount(2, because: "only rows 1 and 3 satisfy the specification");
    }

    [Fact]
    public async Task GetAllForLookupAsync_WithTheAsyncHookOverridden_PassesTheSpecificationCriteria()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        Specification<ReadScopeEntity, int> specification = OddRows();
        AsyncScopedReadController sut = Create(
            (q, l) => new AsyncScopedReadController(q, l, specification), queryService);

        ActionResult<CollectionResult<BaseLookup<int>>> result =
            await sut.GetAllForLookupAsync("Name", CancellationToken.None);

        queryService.LookupPredicatesSeen.Should().ContainSingle().Which.Should().BeSameAs(
            specification.Criteria,
            because: "the lookup contract takes a predicate, so the scope travels as the specification's own expression");
        var lookups = (result.Result as OkObjectResult)!.Value as CollectionResult<BaseLookup<int>>;
        lookups!.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByIdAsync_WithTheAsyncHookOverridden_PassesTheSpecification()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        Specification<ReadScopeEntity, int> specification = OddRows();
        AsyncScopedReadController sut = Create(
            (q, l) => new AsyncScopedReadController(q, l, specification), queryService);

        ActionResult<ReadScopeDTO> result = await sut.GetByIdAsync(3, cancellationToken: CancellationToken.None);

        queryService.SpecificationsSeen.Should().ContainSingle().Which.Should().BeSameAs(specification);
        result.Result.Should().BeOfType<OkObjectResult>(because: "row 3 satisfies the specification");
    }

    [Fact]
    public async Task GetByIdAsync_WhenTheSpecificationExcludesTheRow_Returns404NotFound()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        AsyncScopedReadController sut = Create(
            (q, l) => new AsyncScopedReadController(q, l, OddRows()), queryService);

        ActionResult<ReadScopeDTO> result = await sut.GetByIdAsync(2, cancellationToken: CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(
            StatusCodes.Status404NotFound,
            because: "a 403 on an existing-but-filtered row would confirm the id exists");
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    [Fact]
    public async Task ExportAsync_WithTheAsyncHookOverridden_FiltersEveryPage()
    {
        var queryService = new RecordingQueryService(1, 2, 3, 4, 5);
        Specification<ReadScopeEntity, int> specification = OddRows();
        AsyncScopedReadController sut = Create(
            (q, l) => new AsyncScopedReadController(q, l, specification), queryService, maxPageSize: 2);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        queryService.SpecificationsSeen.Should().HaveCount(2, because: "three matching rows at two per page take two queries");
        queryService.SpecificationsSeen.Should().AllSatisfy(seen => seen.Should().BeSameAs(specification));
        string[] lines = BodyLines();
        lines.Should().HaveCount(4, because: "a header row plus the three odd-numbered rows");
        lines[1..].Select(line => line.Split(',')[0]).Should().Equal("1", "3", "5");
    }

    [Fact]
    public async Task GetAllAsync_ResolvesTheSpecificationOncePerRequest_WithTheRequestToken()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        using var cts = new CancellationTokenSource();
        AsyncScopedReadController sut = Create(
            (q, l) => new AsyncScopedReadController(q, l, OddRows()), queryService);

        await sut.GetAllAsync(fields: null, includeFKs: false, includeChildren: false, cancellationToken: cts.Token);

        sut.HookCallCount.Should().Be(1);
        sut.TokensSeen.Should().ContainSingle().Which.Should().Be(cts.Token);
    }

    // ── The synchronous hook still works, and now scopes the list too ──
    [Fact]
    public async Task GetAllPaged_WithOnlyTheSyncHookOverridden_PassesTheSpecification()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        Specification<ReadScopeEntity, int> specification = OddRows();
        SyncScopedReadController sut = Create(
            (q, l) => new SyncScopedReadController(q, l, specification), queryService);

        await sut.GetAllAsync(pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

        queryService.SpecificationsSeen.Should().ContainSingle().Which.Should().BeSameAs(
            specification,
            because: "the async hook defaults to the sync one, so a controller on the old hook scopes every read");
    }

    [Fact]
    public async Task ExportAsync_WithOnlyTheSyncHookOverridden_StillFiltersTheExport()
    {
        var queryService = new RecordingQueryService(1, 2, 3, 4, 5);
        Specification<ReadScopeEntity, int> specification = OddRows();
        SyncScopedReadController sut = Create(
            (q, l) => new SyncScopedReadController(q, l, specification), queryService, maxPageSize: 10);

        await sut.ExportAsync(cancellationToken: CancellationToken.None);

        queryService.SpecificationsSeen.Should().AllSatisfy(seen => seen.Should().BeSameAs(specification));
        BodyLines().Should().HaveCount(4, because: "a header row plus the three odd-numbered rows");
    }

    [Fact]
    public async Task GetByIdAsync_WhenBothHooksAreOverridden_TheAsyncOneWins()
    {
        var queryService = new RecordingQueryService(1, 2, 3);
        Specification<ReadScopeEntity, int> asyncSpecification = OddRows();
        Specification<ReadScopeEntity, int> syncSpecification = OddRows();
        BothHooksReadController sut = Create(
            (q, l) => new BothHooksReadController(q, l, asyncSpecification, syncSpecification), queryService);

        await sut.GetByIdAsync(1, cancellationToken: CancellationToken.None);

        queryService.SpecificationsSeen.Should().ContainSingle().Which.Should().BeSameAs(
            asyncSpecification,
            because: "an override of the async hook replaces the default that consulted the sync one");
    }

    // ── The ETag emitter is reachable from a derived controller ──
    [Fact]
    public void SetConcurrencyETag_IsCallableFromADerivedController()
    {
        var queryService = new RecordingQueryService(1);
        UnscopedReadController sut = Create(
            (q, l) => new UnscopedReadController(q, l), queryService);

        sut.InvokeSetConcurrencyETag(new ReadScopeDTO { Id = 1, Name = "One", RowVersion = [0, 0, 0, 0, 0, 0, 7, 209] });

        sut.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString().Should().Be(
            "W/\"AAAAAAAAB9E=\"",
            because: "a controller serving a row from its own read action must not have to re-implement the header");
    }

    private string[] BodyLines() =>
        Encoding.UTF8.GetString(_body.ToArray())
            .TrimStart('﻿')
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>An entity used only by the read-scoping tests.</summary>
public sealed class ReadScopeEntity : AuditableBaseEntity<int>;

/// <summary>Its DTO, carrying a concurrency token so the ETag emitter has something to write.</summary>
public sealed record ReadScopeDTO : IBaseDTO<int>
{
    /// <inheritdoc />
    public required int Id { get; init; }

    /// <summary>Gets the display name, which is also the export's second column.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the concurrency token.</summary>
    public byte[]? RowVersion { get; init; }
}

/// <summary>A controller that overrides neither hook: the framework default, unscoped.</summary>
public sealed class UnscopedReadController(
    IEntityQueryService<ReadScopeEntity, ReadScopeDTO, int> queryService,
    ILogger<EntityControllerBase<ReadScopeEntity, ReadScopeDTO, int>> logger)
    : EntityControllerBase<ReadScopeEntity, ReadScopeDTO, int>(queryService, logger)
{
    /// <summary>Proves the widened ETag emitter is reachable from a derived controller.</summary>
    /// <param name="dto">The row just served.</param>
    public void InvokeSetConcurrencyETag(object? dto) => SetConcurrencyETag(dto);
}

/// <summary>
/// A controller that resolves its scope asynchronously, standing in for the consumer controllers
/// that resolve one through a query handler or a claim lookup.
/// </summary>
public sealed class AsyncScopedReadController(
    IEntityQueryService<ReadScopeEntity, ReadScopeDTO, int> queryService,
    ILogger<EntityControllerBase<ReadScopeEntity, ReadScopeDTO, int>> logger,
    Specification<ReadScopeEntity, int>? specification)
    : EntityControllerBase<ReadScopeEntity, ReadScopeDTO, int>(queryService, logger)
{
    /// <summary>Gets how many times the hook was consulted, so a test can prove it resolves once per request.</summary>
    public int HookCallCount { get; private set; }

    /// <summary>Gets the cancellation tokens the hook was handed, in call order.</summary>
    public List<CancellationToken> TokensSeen { get; } = [];

    /// <inheritdoc />
    protected override async ValueTask<Specification<ReadScopeEntity, int>?> GetReadSpecificationAsync(
        CancellationToken cancellationToken)
    {
        HookCallCount++;
        TokensSeen.Add(cancellationToken);

        // A real override awaits a query handler here; yielding keeps the test honest about the
        // hook being genuinely asynchronous rather than a synchronous ValueTask in disguise.
        await Task.Yield();
        return specification;
    }
}

/// <summary>A controller still on the synchronous export hook, which the default now folds in.</summary>
public sealed class SyncScopedReadController(
    IEntityQueryService<ReadScopeEntity, ReadScopeDTO, int> queryService,
    ILogger<EntityControllerBase<ReadScopeEntity, ReadScopeDTO, int>> logger,
    Specification<ReadScopeEntity, int>? specification)
    : EntityControllerBase<ReadScopeEntity, ReadScopeDTO, int>(queryService, logger)
{
    /// <inheritdoc />
    protected override Specification<ReadScopeEntity, int>? GetExportSpecification() => specification;
}

/// <summary>A controller overriding both hooks, to pin down which one the reads follow.</summary>
public sealed class BothHooksReadController(
    IEntityQueryService<ReadScopeEntity, ReadScopeDTO, int> queryService,
    ILogger<EntityControllerBase<ReadScopeEntity, ReadScopeDTO, int>> logger,
    Specification<ReadScopeEntity, int>? asyncSpecification,
    Specification<ReadScopeEntity, int>? syncSpecification)
    : EntityControllerBase<ReadScopeEntity, ReadScopeDTO, int>(queryService, logger)
{
    /// <inheritdoc />
    protected override ValueTask<Specification<ReadScopeEntity, int>?> GetReadSpecificationAsync(
        CancellationToken cancellationToken) => ValueTask.FromResult(asyncSpecification);

    /// <inheritdoc />
    protected override Specification<ReadScopeEntity, int>? GetExportSpecification() => syncSpecification;
}

/// <summary>
/// Query-service stand-in that honors the specification (and the lookup predicate) it is handed and
/// records every one it sees, so a test can prove which scope reached which read.
/// </summary>
public sealed class RecordingQueryService(params int[] ids)
    : IEntityQueryService<ReadScopeEntity, ReadScopeDTO, int>
{
    private readonly List<ReadScopeEntity> _rows = [.. ids.Select(id => new ReadScopeEntity { Id = id })];

    /// <summary>Gets the specification passed to each specification-taking query, in call order.</summary>
    public List<ISpecification<ReadScopeEntity, int>?> SpecificationsSeen { get; } = [];

    /// <summary>Gets the predicate passed to each lookup query, in call order.</summary>
    public List<Expression<Func<ReadScopeEntity, bool>>?> LookupPredicatesSeen { get; } = [];

    /// <inheritdoc />
    public IEntityDTOMapper<ReadScopeEntity, ReadScopeDTO, int> DTOMapper => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<PagedCollectionResult<object>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        ISpecification<ReadScopeEntity, int>? specification = null,
        Dictionary<string, (string Operator, string Value)>? filters = null,
        string? sortColumn = null,
        string? sortDirection = null,
        string? fields = null,
        int? pageNumber = null,
        int? pageSize = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        SpecificationsSeen.Add(specification);

        List<ReadScopeEntity> matching = Matching(specification);
        int size = Math.Max(1, pageSize ?? matching.Count);
        int page = Math.Max(1, pageNumber ?? 1);

        IReadOnlyCollection<object> items =
        [
            .. matching.Skip((page - 1) * size).Take(size).Select(row => (object)Dto(row))
        ];

        return Task.FromResult(Result.Success(
            new PagedCollectionResult<object>(items, new PaginationMetadata(matching.Count, size, page))));
    }

    /// <inheritdoc />
    public Task<Result<PagedCollectionResult<object>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        ISpecification<ReadScopeEntity, int>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<IReadOnlyCollection<BaseLookup<int>>>> GetAllForLookupAsync(
        string nameProperty,
        Expression<Func<ReadScopeEntity, bool>>? where = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        LookupPredicatesSeen.Add(where);

        Func<ReadScopeEntity, bool>? predicate = where?.Compile();
        IReadOnlyCollection<BaseLookup<int>> lookups =
        [
            .. _rows
                .Where(row => predicate is null || predicate(row))
                .Select(row => new BaseLookup<int> { Id = row.Id, Name = Name(row) })
        ];

        return Task.FromResult(Result.Success(lookups));
    }

    /// <inheritdoc />
    public Task<Result<object>> GetByIdAsync(
        int id,
        bool includeFKs = false,
        bool includeChildren = false,
        ISpecification<ReadScopeEntity, int>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        SpecificationsSeen.Add(specification);

        ReadScopeEntity? row = Matching(specification).Find(candidate => candidate.Id == id);

        // A row the specification filtered out is simply absent from the query, which is the same
        // NotFound the real query service reports for an id that never existed.
        return Task.FromResult(row is null
            ? Result.Failure<object>(Error.NotFoundError("Test.NotFound", "Entity not found"))
            : Result.Success<object>(Dto(row)));
    }

    /// <inheritdoc />
    public Task<Result<ReadScopeEntity>> GetEntityByIdAsync(
        string idValue,
        string? idField = null,
        bool includeFKs = false,
        bool includeChildren = false,
        ISpecification<ReadScopeEntity, int>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<bool> ExistsAsync(
        Expression<Func<ReadScopeEntity, bool>> where,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private List<ReadScopeEntity> Matching(ISpecification<ReadScopeEntity, int>? specification) =>
        [.. _rows.Where(row => specification is null || specification.IsSatisfiedBy(row))];

    private static ReadScopeDTO Dto(ReadScopeEntity row) =>
        new() { Id = row.Id, Name = Name(row) };

    private static string Name(ReadScopeEntity row) =>
        string.Create(CultureInfo.InvariantCulture, $"Name {row.Id}");
}
