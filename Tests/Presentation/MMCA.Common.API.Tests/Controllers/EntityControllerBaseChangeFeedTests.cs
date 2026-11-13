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
using Moq;

namespace MMCA.Common.API.Tests.Controllers;

/// <summary>
/// The opt-in change-feed action: absent unless the controller carries
/// <see cref="SupportsChangeFeedAttribute"/>, and when present a thin edge over the query service's
/// changed-since read (read scope, page-size clamp, 200 or Problem Details).
/// </summary>
public sealed class EntityControllerBaseChangeFeedTests
{
    private const string ChangesSegment = "/changes";

    private static readonly DateTime Since = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>>> _logger = new();
    private readonly PagingQueryService _queryService = new();

    // -- Rule 1: no opt-in, no endpoint, identical document --
    [Fact]
    public async Task WithoutOptIn_NoChangesEndpoint_DocumentIdentical()
    {
        OpenApiProbeSurface withoutOptIn = await OpenApiProbeSurface.CaptureAsync(typeof(ChangeFeedWithoutOptIn.FeedItemsController));
        OpenApiProbeSurface reference = await OpenApiProbeSurface.CaptureAsync(typeof(ChangeFeedReference.FeedItemsController));

        withoutOptIn.Exposes(ChangesSegment).Should().BeFalse("a controller that did not opt in has no changes route and no ApiDescription");
        withoutOptIn.Document.Should().NotContain("/changes");
        withoutOptIn.Document.Should().Be(
            reference.Document,
            "the document must be byte-identical to one generated from a controller where the action does not exist at all");
    }

    [Fact]
    public async Task WithOptIn_Routed()
    {
        OpenApiProbeSurface surface = await OpenApiProbeSurface.CaptureAsync(typeof(ChangeFeedOptInController));

        surface.Routes.Should().Contain(route => route.EndsWith("ChangeFeedOptIn/changes", StringComparison.OrdinalIgnoreCase));
        surface.ApiPaths.Should().Contain(path => path.EndsWith("ChangeFeedOptIn/changes", StringComparison.OrdinalIgnoreCase));

        using var document = JsonDocument.Parse(surface.Document);
        document.RootElement.GetProperty("paths").TryGetProperty("/ChangeFeedOptIn/changes", out JsonElement pathItem)
            .Should().BeTrue("an opted-in controller documents the changes operation");
        pathItem.TryGetProperty("get", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CursorOptIn_DoesNotExposeTheChangeFeed()
    {
        OpenApiProbeSurface surface = await OpenApiProbeSurface.CaptureAsync(typeof(CursorOptInController));

        surface.Exposes(ChangesSegment).Should().BeFalse("each opt-in exposes only its own action");
    }

    [Fact]
    public void OptInAction_RemovesTheChangesAction_WithoutItsOptIn()
    {
        MethodInfo method = typeof(EntityControllerBase<PagingEntity, PagingDTO, int>)
            .GetMethod(nameof(EntityControllerBase<,,>.GetChangesAsync))!;
        var controller = new ControllerModel(
            typeof(ChangeFeedWithoutOptIn.FeedItemsController).GetTypeInfo(),
            [new SupportsCursorPagingAttribute()]);
        var action = new ActionModel(method, []) { Controller = controller };
        controller.Actions.Add(action);

        new OptInActionAttribute(typeof(SupportsChangeFeedAttribute)).Apply(action);

        controller.Actions.Should().BeEmpty("the cursor opt-in does not stand in for the change-feed opt-in");
    }

    [Fact]
    public void SupportsChangeFeed_IsAnInheritedSingleUseClassAttribute()
    {
        AttributeUsageAttribute usage = typeof(SupportsChangeFeedAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;

        usage.ValidOn.Should().Be(AttributeTargets.Class);
        usage.AllowMultiple.Should().BeFalse();
        usage.Inherited.Should().BeTrue();
    }

    // -- Rule 2: scope resolved once, page size clamped, arguments passed --
    [Fact]
    public async Task ReadSpecification_IsPassed()
    {
        var specification = new InlineSpecification<PagingEntity, int>(e => e.Id > 10);
        using var cts = new CancellationTokenSource();
        ScopedChangeFeedController sut = Create((q, l) => new ScopedChangeFeedController(q, l, specification));

        await sut.GetChangesAsync(since: Since, cursor: "c1", pageSize: 20, cancellationToken: cts.Token);

        ChangeFeedCall call = _queryService.ChangeFeedCalls.Should().ContainSingle().Subject;
        call.Specification.Should().BeSameAs(specification);
        call.CancellationToken.Should().Be(cts.Token);
        call.Since.Should().Be(Since);
        call.Cursor.Should().Be("c1");
        call.PageSize.Should().Be(20);
        sut.HookCallCount.Should().Be(1, "the scope is resolved once per request");
    }

    [Theory]
    [InlineData(5000, 50)]
    [InlineData(100, 50)]
    [InlineData(20, 20)]
    public async Task PageSize_Clamped(int requested, int expected)
    {
        ChangeFeedOptInController sut = Create((q, l) => new ChangeFeedOptInController(q, l), maxPageSize: 50);

        await sut.GetChangesAsync(since: null, cursor: null, pageSize: requested, cancellationToken: CancellationToken.None);

        _queryService.ChangeFeedCalls.Should().ContainSingle().Which.PageSize.Should().Be(expected);
    }

    [Fact]
    public void PageSize_DefaultsToOneHundredAndIsRangeValidated()
    {
        ParameterInfo[] parameters = typeof(EntityControllerBase<PagingEntity, PagingDTO, int>)
            .GetMethod(nameof(EntityControllerBase<,,>.GetChangesAsync))!
            .GetParameters();

        parameters.Select(p => p.Name).Should().Equal("since", "cursor", "pageSize", "cancellationToken");
        ParameterInfo pageSize = parameters.Single(p => p.Name == "pageSize");
        pageSize.DefaultValue.Should().Be(100);
        RangeAttribute range = pageSize.GetCustomAttribute<RangeAttribute>()!;
        range.Minimum.Should().Be(1);
        range.Maximum.Should().Be(int.MaxValue);
    }

    // -- Rule 2: the answer --
    [Fact]
    public async Task Success_Returns200()
    {
        var page = new KeysetCollectionResult<EntityChange<PagingDTO, int>>(
            [
                new EntityChange<PagingDTO, int> { Id = 1, IsDeleted = false, ChangedOn = Since, Item = new PagingDTO { Id = 1, Name = "a" } },
                new EntityChange<PagingDTO, int> { Id = 2, IsDeleted = true, ChangedOn = Since.AddMinutes(1), Item = null },
            ],
            nextCursor: "next");
        _queryService.ChangeFeedResult = Result.Success(page);
        ChangeFeedOptInController sut = Create((q, l) => new ChangeFeedOptInController(q, l));

        ActionResult<KeysetCollectionResult<EntityChange<PagingDTO, int>>> result =
            await sut.GetChangesAsync(since: Since, cursor: null, pageSize: 10, cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(page, "the feed page is served as the query service produced it");
    }

    [Theory]
    [InlineData("validation", StatusCodes.Status400BadRequest)]
    [InlineData("unexpected", StatusCodes.Status500InternalServerError)]
    public async Task Failure_ReturnsProblemDetails(string kind, int expectedStatus)
    {
        Error error = kind == "validation"
            ? Error.Validation("Error.InvalidCursor", "The supplied pagination cursor isn't valid.")
            : Error.Unexpected(IEntityQueryService<PagingEntity, PagingDTO, int>.ChangeFeedNotSupportedErrorCode, "Not supported.");
        _queryService.ChangeFeedResult = Result.Failure<KeysetCollectionResult<EntityChange<PagingDTO, int>>>(error);
        ChangeFeedOptInController sut = Create((q, l) => new ChangeFeedOptInController(q, l));

        ActionResult<KeysetCollectionResult<EntityChange<PagingDTO, int>>> result =
            await sut.GetChangesAsync(since: null, cursor: "bad", pageSize: 10, cancellationToken: CancellationToken.None);

        var objectResult = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(expectedStatus);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        JsonSerializer.Serialize(problem.Extensions["errors"]).Should().Contain(error.Code);
    }

    // -- Helpers --
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

/// <summary>A controller that opted in to the change feed.</summary>
[SupportsChangeFeed]
public sealed class ChangeFeedOptInController(
    IEntityQueryService<PagingEntity, PagingDTO, int> queryService,
    ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>> logger)
    : EntityControllerBase<PagingEntity, PagingDTO, int>(queryService, logger);

/// <summary>An opted-in controller that scopes its reads and counts the hook calls.</summary>
[SupportsChangeFeed]
public sealed class ScopedChangeFeedController(
    IEntityQueryService<PagingEntity, PagingDTO, int> queryService,
    ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>> logger,
    Specification<PagingEntity, int>? specification)
    : EntityControllerBase<PagingEntity, PagingDTO, int>(queryService, logger)
{
    /// <summary>Gets how many times the hook was consulted.</summary>
    public int HookCallCount { get; private set; }

    /// <inheritdoc />
    protected override ValueTask<Specification<PagingEntity, int>?> GetReadSpecificationAsync(
        CancellationToken cancellationToken)
    {
        HookCallCount++;
        return ValueTask.FromResult(specification);
    }
}

/// <summary>
/// Holds the non-adopting controller of the document-equality test. It shares its simple name with
/// the controller in <see cref="ChangeFeedReference"/>, so both documents carry the same route and tag.
/// </summary>
public static class ChangeFeedWithoutOptIn
{
    // No XML summary on purpose: the host's XML-comment generator must see the two twins identically.
    public sealed class FeedItemsController(
        IEntityQueryService<PagingEntity, PagingDTO, int> queryService,
        ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>> logger)
        : EntityControllerBase<PagingEntity, PagingDTO, int>(queryService, logger);
}

/// <summary>
/// Holds the reference twin: the changes action is excluded from MVC entirely with
/// <see cref="NonActionAttribute"/>, so its document is what "the action does not exist" looks like.
/// </summary>
public static class ChangeFeedReference
{
    // No XML summary on purpose: the host's XML-comment generator must see the two twins identically.
    public sealed class FeedItemsController(
        IEntityQueryService<PagingEntity, PagingDTO, int> queryService,
        ILogger<EntityControllerBase<PagingEntity, PagingDTO, int>> logger)
        : EntityControllerBase<PagingEntity, PagingDTO, int>(queryService, logger)
    {
        // Not an action at all; the body is never reached.
        [NonAction]
        public override Task<ActionResult<KeysetCollectionResult<EntityChange<PagingDTO, int>>>> GetChangesAsync(
            DateTime? since = null,
            string? cursor = null,
            int pageSize = 100,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
