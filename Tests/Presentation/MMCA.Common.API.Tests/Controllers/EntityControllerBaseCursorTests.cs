using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Controllers;
using MMCA.Common.API.Pagination;
using MMCA.Common.API.Tests.Fakes;
using MMCA.Common.API.Tests.OpenApi;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;
using Moq;

namespace MMCA.Common.API.Tests.Controllers;

/// <summary>
/// The opt-in cursor list action: absent (route, ApiExplorer and document) unless the controller
/// carries <see cref="SupportsCursorPagingAttribute"/>, and when present a thin edge over the query
/// service's keyset read (direction check, page-size clamp, read scope, 200 or Problem Details).
/// </summary>
public sealed class EntityControllerBaseCursorTests
{
    private const string CursorSegment = "/cursor";

    private readonly Mock<ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>>> _logger = new();
    private readonly PagingQueryService _queryService = new();

    // -- Rule 1: the self-removing action convention --
    [Fact]
    public void OptInAction_RemovesItsAction_WhenTheControllerLacksTheOptIn()
    {
        (ControllerModel controller, ActionModel action) = BuildModel(controllerAttributes: []);

        new OptInActionAttribute(typeof(SupportsCursorPagingAttribute)).Apply(action);

        controller.Actions.Should().NotContain(action);
    }

    [Fact]
    public void OptInAction_KeepsItsAction_WhenTheControllerCarriesTheOptIn()
    {
        (ControllerModel controller, ActionModel action) = BuildModel(controllerAttributes: [new SupportsCursorPagingAttribute()]);

        new OptInActionAttribute(typeof(SupportsCursorPagingAttribute)).Apply(action);

        controller.Actions.Should().ContainSingle().Which.Should().BeSameAs(action);
    }

    [Fact]
    public void OptInAction_ExposesItsOptInType() =>
        new OptInActionAttribute(typeof(SupportsCursorPagingAttribute)).OptInAttributeType
            .Should().Be<SupportsCursorPagingAttribute>();

    [Fact]
    public void SupportsCursorPaging_IsAnInheritedSingleUseClassAttribute()
    {
        AttributeUsageAttribute usage = typeof(SupportsCursorPagingAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;

        usage.ValidOn.Should().Be(AttributeTargets.Class);
        usage.AllowMultiple.Should().BeFalse();
        usage.Inherited.Should().BeTrue("an opt-in on an abstract base must reach every derived controller");
    }

    // -- Rule 2: no opt-in, no endpoint, identical document --
    [Fact]
    public async Task WithoutOptIn_NoCursorEndpoint_AndDocumentIdentical()
    {
        OpenApiProbeSurface withoutOptIn = await OpenApiProbeSurface.CaptureAsync(typeof(CursorWithoutOptIn.CursorItemsController));
        OpenApiProbeSurface reference = await OpenApiProbeSurface.CaptureAsync(typeof(CursorReference.CursorItemsController));

        withoutOptIn.Exposes(CursorSegment).Should().BeFalse("a controller that did not opt in has no cursor route and no ApiDescription");
        withoutOptIn.Document.Should().NotContain("/cursor");
        withoutOptIn.Document.Should().Be(
            reference.Document,
            "the document must be byte-identical to one generated from a controller where the action does not exist at all");
    }

    [Fact]
    public async Task WithOptIn_CursorEndpointRouted()
    {
        OpenApiProbeSurface surface = await OpenApiProbeSurface.CaptureAsync(typeof(CursorOptInController));

        surface.Routes.Should().Contain(route => route.EndsWith("CursorOptIn/cursor", StringComparison.OrdinalIgnoreCase));
        surface.ApiPaths.Should().Contain(path => path.EndsWith("CursorOptIn/cursor", StringComparison.OrdinalIgnoreCase));

        using var document = JsonDocument.Parse(surface.Document);
        document.RootElement.GetProperty("paths").TryGetProperty("/CursorOptIn/cursor", out JsonElement pathItem)
            .Should().BeTrue("an opted-in controller documents the cursor operation");
        pathItem.TryGetProperty("get", out _).Should().BeTrue();
    }

    [Fact]
    public async Task OverrideWithoutOptIn_StillRemoved()
    {
        OpenApiProbeSurface surface = await OpenApiProbeSurface.CaptureAsync(typeof(CursorOverrideController));

        surface.Exposes(CursorSegment).Should().BeFalse(
            "an override inherits the base action's convention, so overriding is not a way around the opt-in");
    }

    [Fact]
    public async Task OptInOnAbstractBase_AppliesToDerived()
    {
        OpenApiProbeSurface surface = await OpenApiProbeSurface.CaptureAsync(typeof(CursorInheritedController));

        surface.Routes.Should().Contain(route => route.EndsWith("CursorInherited/cursor", StringComparison.OrdinalIgnoreCase),
            "the opt-in declared on an abstract base counts for the derived controller");
    }

    // -- Rule 3: the direction is validated at the edge --
    [Fact]
    public async Task InvalidSortDirection_Returns400()
    {
        CursorOptInController sut = Create((q, l) => new CursorOptInController(q, l));

        ActionResult<KeysetCollectionResult<PagingDTO>> result = await sut.GetCursorPageAsync(
            sortColumn: null, sortDirection: "sideways", cursor: null, pageSize: 10, cancellationToken: CancellationToken.None);

        var objectResult = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        JsonSerializer.Serialize(problem.Extensions["errors"]).Should().Contain("Error.InvalidSortDirection");
        _queryService.CursorCalls.Should().BeEmpty("an invalid direction never reaches the query service");
    }

    // -- Rule 6: only "desc" (any case) orders descending --
    [Theory]
    [InlineData("desc", true)]
    [InlineData("DESC", true)]
    [InlineData("Desc", true)]
    [InlineData("asc", false)]
    [InlineData("ASC", false)]
    [InlineData(null, false)]
    public async Task Desc_SetsDescending(string? sortDirection, bool expectedDescending)
    {
        CursorOptInController sut = Create((q, l) => new CursorOptInController(q, l));

        await sut.GetCursorPageAsync(
            sortColumn: "name", sortDirection: sortDirection, cursor: "abc", pageSize: 10, cancellationToken: CancellationToken.None);

        KeysetPageRequest request = _queryService.CursorCalls.Should().ContainSingle().Subject.Request;
        request.Descending.Should().Be(expectedDescending);
        request.SortColumn.Should().Be("name", "the sort column travels to the query service, which validates and maps it");
        request.Cursor.Should().Be("abc");
    }

    // -- Rule 4: the page size is clamped to MaxPageSize --
    [Theory]
    [InlineData(5000, 50)]
    [InlineData(50, 50)]
    [InlineData(20, 20)]
    public async Task PageSize_ClampedToMaxPageSize(int requested, int expected)
    {
        CursorOptInController sut = Create((q, l) => new CursorOptInController(q, l), maxPageSize: 50);

        await sut.GetCursorPageAsync(
            sortColumn: null, sortDirection: null, cursor: null, pageSize: requested, cancellationToken: CancellationToken.None);

        _queryService.CursorCalls.Should().ContainSingle().Which.Request.PageSize.Should().Be(expected);
    }

    [Fact]
    public void PageSize_DefaultsToTenAndIsRangeValidated()
    {
        ParameterInfo pageSize = typeof(EntityControllerBase<PagingEntity, PagingDTO, int>)
            .GetMethod(nameof(EntityControllerBase<,,>.GetCursorPageAsync))!
            .GetParameters()
            .Single(p => p.Name == "pageSize");

        pageSize.DefaultValue.Should().Be(10);
        RangeAttribute range = pageSize.GetCustomAttribute<RangeAttribute>()!;
        range.Minimum.Should().Be(1);
        range.Maximum.Should().Be(int.MaxValue);
    }

    // -- Rule 5: the read scope is resolved once and passed --
    [Fact]
    public async Task ReadSpecification_IsApplied()
    {
        var specification = new InlineSpecification<PagingEntity, int>(e => e.Id % 2 == 1);
        using var cts = new CancellationTokenSource();
        ScopedCursorController sut = Create((q, l) => new ScopedCursorController(q, l, specification));

        await sut.GetCursorPageAsync(
            sortColumn: null, sortDirection: null, cursor: null, pageSize: 10, cancellationToken: cts.Token);

        CursorPageCall call = _queryService.CursorCalls.Should().ContainSingle().Subject;
        call.Specification.Should().BeSameAs(specification);
        call.CancellationToken.Should().Be(cts.Token);
        sut.HookCallCount.Should().Be(1, "the scope is resolved once per request");
        sut.TokensSeen.Should().ContainSingle().Which.Should().Be(cts.Token);
    }

    [Fact]
    public async Task NoOverride_PassesNoSpecification()
    {
        CursorOptInController sut = Create((q, l) => new CursorOptInController(q, l));

        await sut.GetCursorPageAsync(
            sortColumn: null, sortDirection: null, cursor: null, pageSize: 10, cancellationToken: CancellationToken.None);

        _queryService.CursorCalls.Should().ContainSingle().Which.Specification.Should().BeNull();
    }

    // -- Rule 7: 200 with the page, no pagination header, no ETag --
    [Fact]
    public async Task Success_Returns200WithItemsAndNextCursor()
    {
        var page = new KeysetCollectionResult<PagingDTO>(
            [new PagingDTO { Id = 3, Name = "c" }, new PagingDTO { Id = 1, Name = "a" }],
            nextCursor: "next-cursor");
        _queryService.CursorResult = Result.Success(page);
        CursorOptInController sut = Create((q, l) => new CursorOptInController(q, l));

        ActionResult<KeysetCollectionResult<PagingDTO>> result = await sut.GetCursorPageAsync(
            sortColumn: null, sortDirection: null, cursor: null, pageSize: 10, cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<KeysetCollectionResult<PagingDTO>>().Subject;
        body.Items.Select(i => i.Id).Should().Equal(3, 1);
        body.NextCursor.Should().Be("next-cursor");
        sut.Response.Headers.ContainsKey(PaginationHeaderExtensions.HeaderName).Should().BeFalse(
            "a keyset page has no total, so there is no pagination header");
        sut.Response.Headers.ContainsKey(ConcurrencyETag.ETagHeaderName).Should().BeFalse("a list carries no ETag");
    }

    [Theory]
    [InlineData("validation", StatusCodes.Status400BadRequest)]
    [InlineData("unexpected", StatusCodes.Status500InternalServerError)]
    public async Task QueryFailure_ReturnsProblemDetails(string kind, int expectedStatus)
    {
        Error error = kind == "validation"
            ? Error.Validation("Error.InvalidCursor", "The supplied pagination cursor isn't valid.")
            : Error.Unexpected(IEntityQueryService<PagingEntity, PagingDTO, int>.CursorPagingNotSupportedErrorCode, "Not supported.");
        _queryService.CursorResult = Result.Failure<KeysetCollectionResult<PagingDTO>>(error);
        CursorOptInController sut = Create((q, l) => new CursorOptInController(q, l));

        ActionResult<KeysetCollectionResult<PagingDTO>> result = await sut.GetCursorPageAsync(
            sortColumn: null, sortDirection: null, cursor: "bad", pageSize: 10, cancellationToken: CancellationToken.None);

        var objectResult = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(expectedStatus);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        JsonSerializer.Serialize(problem.Extensions["errors"]).Should().Contain(error.Code);
    }

    // -- Helpers --
    private static (ControllerModel Controller, ActionModel Action) BuildModel(object[] controllerAttributes)
    {
        MethodInfo method = typeof(EntityControllerBase<PagingEntity, PagingDTO, int>)
            .GetMethod(nameof(EntityControllerBase<,,>.GetCursorPageAsync))!;
        var controller = new ControllerModel(typeof(CursorWithoutOptIn.CursorItemsController).GetTypeInfo(), controllerAttributes);
        var action = new ActionModel(method, []) { Controller = controller };
        controller.Actions.Add(action);
        return (controller, action);
    }

    private TController Create<TController>(
        Func<IEntityQueryService<PagingEntity, PagingDTO, int>, ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>>, TController> factory,
        int maxPageSize = 50)
        where TController : ControllerBase
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ApplicationSettings { MaxPageSize = maxPageSize }));

        TController controller = factory(_queryService, _logger.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() },
        };
        return controller;
    }
}

/// <summary>A controller that opted in to the cursor action.</summary>
[SupportsCursorPaging]
public sealed class CursorOptInController(
    IEntityQueryService<PagingEntity, PagingDTO, int> queryService,
    ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>> logger)
    : EntityControllerBase<PagingEntity, PagingDTO, int>(queryService, logger);

/// <summary>A controller that overrides the cursor action without opting in.</summary>
public sealed class CursorOverrideController(
    IEntityQueryService<PagingEntity, PagingDTO, int> queryService,
    ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>> logger)
    : EntityControllerBase<PagingEntity, PagingDTO, int>(queryService, logger)
{
    /// <inheritdoc />
    public override Task<ActionResult<KeysetCollectionResult<PagingDTO>>> GetCursorPageAsync(
        string? sortColumn = null,
        string? sortDirection = null,
        string? cursor = null,
        [Range(1, int.MaxValue)] int pageSize = 10,
        CancellationToken cancellationToken = default) =>
        base.GetCursorPageAsync(sortColumn, sortDirection, cursor, pageSize, cancellationToken);
}

/// <summary>An abstract base that carries the opt-in for every controller derived from it.</summary>
[SupportsCursorPaging]
public abstract class CursorPagedControllerBase(
    IEntityQueryService<PagingEntity, PagingDTO, int> queryService,
    ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>> logger)
    : EntityControllerBase<PagingEntity, PagingDTO, int>(queryService, logger);

/// <summary>A controller that declares nothing itself and inherits the opt-in.</summary>
public sealed class CursorInheritedController(
    IEntityQueryService<PagingEntity, PagingDTO, int> queryService,
    ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>> logger)
    : CursorPagedControllerBase(queryService, logger);

/// <summary>An opted-in controller that scopes its reads asynchronously and counts the hook calls.</summary>
[SupportsCursorPaging]
public sealed class ScopedCursorController(
    IEntityQueryService<PagingEntity, PagingDTO, int> queryService,
    ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>> logger,
    Specification<PagingEntity, int>? specification)
    : EntityControllerBase<PagingEntity, PagingDTO, int>(queryService, logger)
{
    /// <summary>Gets how many times the hook was consulted.</summary>
    public int HookCallCount { get; private set; }

    /// <summary>Gets the tokens the hook was handed, in call order.</summary>
    public List<CancellationToken> TokensSeen { get; } = [];

    /// <inheritdoc />
    protected override async ValueTask<Specification<PagingEntity, int>?> GetReadSpecificationAsync(
        CancellationToken cancellationToken)
    {
        HookCallCount++;
        TokensSeen.Add(cancellationToken);
        await Task.Yield();
        return specification;
    }
}

/// <summary>
/// Holds the non-adopting controller of the document-equality test. It shares its simple name with
/// the controller in <see cref="CursorReference"/>, so both documents carry the same route and tag.
/// </summary>
public static class CursorWithoutOptIn
{
    // No XML summary on purpose: the host's XML-comment generator must see the two twins identically.
    public sealed class CursorItemsController(
        IEntityQueryService<PagingEntity, PagingDTO, int> queryService,
        ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>> logger)
        : EntityControllerBase<PagingEntity, PagingDTO, int>(queryService, logger);
}

/// <summary>
/// Holds the reference twin: the cursor action is excluded from MVC entirely with
/// <see cref="NonActionAttribute"/>, so its document is what "the action does not exist" looks like.
/// </summary>
public static class CursorReference
{
    // No XML summary on purpose: the host's XML-comment generator must see the two twins identically.
    public sealed class CursorItemsController(
        IEntityQueryService<PagingEntity, PagingDTO, int> queryService,
        ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>> logger)
        : EntityControllerBase<PagingEntity, PagingDTO, int>(queryService, logger)
    {
        // Not an action at all; the body is never reached.
        [NonAction]
        public override Task<ActionResult<KeysetCollectionResult<PagingDTO>>> GetCursorPageAsync(
            string? sortColumn = null,
            string? sortDirection = null,
            string? cursor = null,
            int pageSize = 10,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
