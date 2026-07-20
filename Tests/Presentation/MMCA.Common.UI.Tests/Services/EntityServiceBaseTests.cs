using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Exceptions;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Pins the CRUD verb/route/serialization contract of <see cref="EntityServiceBase{TEntityDTO, TId}"/>
/// through a minimal concrete subclass: GET list (+flags), GET paged (+full query-string building),
/// GET lookup, GET by id (404 as null), POST create (null body throws), PUT update, DELETE, the
/// named APIClient + bearer-token plumbing, and failure mapping through
/// <see cref="ServiceExceptionHelper"/>. Failure responses use 4xx codes only; 5xx would engage the
/// class-level Polly retry backoff (2s/4s/8s).
/// </summary>
public sealed class EntityServiceBaseTests
{
    private sealed record WidgetDto : IBaseDTO<int>
    {
        public required int Id { get; init; }

        public string? Name { get; init; }
    }

    private sealed class WidgetService(IHttpClientFactory httpClientFactory, ITokenStorageService tokenStorageService)
        : EntityServiceBase<WidgetDto, int>("widgets", httpClientFactory, tokenStorageService);

    private sealed record Mocks(StubHttpMessageHandler Handler, StubHttpClientFactory Factory);

    private static (WidgetService Sut, Mocks Mocks) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? token = "stored-access-token")
    {
        var handler = new StubHttpMessageHandler(responder);
        var factory = new StubHttpClientFactory(handler);
        var tokenStorage = new Mock<ITokenStorageService>();
        tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(token);
        return (new WidgetService(factory, tokenStorage.Object), new Mocks(handler, factory));
    }

    private static HttpResponseMessage PagedResponse(int totalItems, params WidgetDto[] items) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                new PagedCollectionResult<WidgetDto>(items, new PaginationMetadata(totalItems, 25, 2))),
        };

    private static WidgetDto Widget(int id, string name = "Widget") => new() { Id = id, Name = name };

    // == GetAllAsync ==
    [Fact]
    public async Task GetAllAsync_RequestsListWithIncludeFlags_ReturnsUnwrappedItems()
    {
        var (sut, mocks) = CreateSut(_ => PagedResponse(2, Widget(1), Widget(2)));

        var result = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        mocks.Factory.LastClientName.Should().Be("APIClient");
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Get);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/widgets?includeFKs=False&includeChildren=False");
        mocks.Handler.LastRequest.Authorization!.ToString().Should().Be("Bearer stored-access-token");
        result.Should().NotBeNull();
        result!.Select(w => w.Id).Should().Equal(1, 2);
    }

    [Fact]
    public async Task GetAllAsync_WithIncludeFlagsSet_EncodesTrueValues()
    {
        var (sut, mocks) = CreateSut(_ => PagedResponse(0));

        await sut.GetAllAsync(includeFKs: true, includeChildren: true, TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/widgets?includeFKs=True&includeChildren=True");
    }

    [Fact]
    public async Task GetAllAsync_WhenBodyIsNull_ReturnsEmptyList()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "null"));

        var result = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Should().NotBeNull().And.BeEmpty();
    }

    // == GetPagedAsync ==
    [Fact]
    public async Task GetPagedAsync_BuildsPagedQueryWithSortAndEscapedFilters()
    {
        var (sut, mocks) = CreateSut(_ => PagedResponse(37, Widget(1)));
        var filters = new Dictionary<string, (string Operator, string Value)>
        {
            ["Name"] = ("contains", "blue shirt"),
        };

        var (items, totalItems) = await sut.GetPagedAsync(
            filters, pageNumber: 2, pageSize: 25, sortColumn: "Name", sortDirection: "desc",
            includeChildren: false, TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be(
            "/widgets/paged?pageNumber=2&pageSize=25&sortColumn=Name&sortDirection=desc&includeChildren=False"
            + "&filters[Name].operator=contains&filters[Name].value=blue%20shirt");
        items.Should().HaveCount(1);
        totalItems.Should().Be(37);
    }

    [Fact]
    public async Task GetPagedAsync_SkipsFiltersWithoutOperatorAndOmitsEmptyValues()
    {
        var (sut, mocks) = CreateSut(_ => PagedResponse(0));
        var filters = new Dictionary<string, (string Operator, string Value)>
        {
            ["Ignored"] = (string.Empty, "value-without-operator"),
            ["Status"] = ("equals", string.Empty),
        };

        await sut.GetPagedAsync(
            filters, pageNumber: 1, pageSize: 10, sortColumn: null, sortDirection: null,
            includeChildren: false, TestContext.Current.CancellationToken);

        var query = mocks.Handler.LastRequest.Uri!.PathAndQuery;
        query.Should().Contain("filters[Status].operator=equals");
        query.Should().NotContain("filters[Status].value");
        query.Should().NotContain("Ignored");
    }

    [Fact]
    public async Task GetPagedAsync_WithoutSort_SendsEmptySortParameters()
    {
        var (sut, mocks) = CreateSut(_ => PagedResponse(0));

        await sut.GetPagedAsync(
            [], pageNumber: 1, pageSize: 10, sortColumn: null, sortDirection: null,
            includeChildren: false, TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be(
            "/widgets/paged?pageNumber=1&pageSize=10&sortColumn=&sortDirection=&includeChildren=False");
    }

    [Fact]
    public async Task GetPagedAsync_WhenBodyIsNull_ReturnsEmptyItemsAndZeroTotal()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "null"));

        var (items, totalItems) = await sut.GetPagedAsync(
            [], pageNumber: 1, pageSize: 10, sortColumn: null, sortDirection: null,
            includeChildren: false, TestContext.Current.CancellationToken);

        items.Should().BeEmpty();
        totalItems.Should().Be(0);
    }

    // == GetAllForLookupAsync ==
    [Fact]
    public async Task GetAllForLookupAsync_RequestsLookupEndpoint_ReturnsLookups()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new CollectionResult<BaseLookup<int>>(
                [new BaseLookup<int> { Id = 1, Name = "First" }])),
        });

        var result = await sut.GetAllForLookupAsync("Name", TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/widgets/lookup?nameProperty=Name");
        result.Should().ContainSingle().Which.Name.Should().Be("First");
    }

    [Fact]
    public async Task GetAllForLookupAsync_EscapesNamePropertyInQueryString()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new CollectionResult<BaseLookup<int>>([])),
        });

        await sut.GetAllForLookupAsync("Display Name&Sort", TestContext.Current.CancellationToken);

        // A space or ampersand in the property name must be percent-encoded, not smuggled into the
        // query string as a separator (same treatment the paged path gives its filter parameters).
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be(
            "/widgets/lookup?nameProperty=Display%20Name%26Sort");
    }

    // == GetByIdAsync ==
    [Fact]
    public async Task GetByIdAsync_RequestsIdRouteWithChildrenFlag_ReturnsDto()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(Widget(7, "Blue")),
        });

        var result = await sut.GetByIdAsync(7, includeChildren: false, TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Get);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/widgets/7?includeChildren=False");
        result.Should().NotBeNull();
        result!.Name.Should().Be("Blue");
    }

    [Fact]
    public async Task GetByIdAsync_OnNotFound_ReturnsNullInsteadOfThrowing()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.GetByIdAsync(999, cancellationToken: TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    // == AddAsync ==
    [Fact]
    public async Task AddAsync_PostsEntityToCollectionRoute_ReturnsCreatedDto()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(Widget(7, "Blue")),
        });

        var result = await sut.AddAsync(Widget(0, "Blue"), TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/widgets");
        mocks.Handler.LastRequest.Body.Should().Contain("Blue");
        result.Id.Should().Be(7);
    }

    [Fact]
    public async Task AddAsync_WhenApiReturnsNullBody_ThrowsInvalidOperation()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "null"));

        var act = () => sut.AddAsync(Widget(0), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*WidgetDto*");
    }

    // == UpdateAsync ==
    [Fact]
    public async Task UpdateAsync_PutsEntityToIdRoute_ReturnsTrue()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.UpdateAsync(Widget(7, "Renamed"), TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Put);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/widgets/7");
        mocks.Handler.LastRequest.Body.Should().Contain("Renamed");
    }

    // == DeleteAsync ==
    [Fact]
    public async Task DeleteAsync_DeletesIdRoute_ReturnsTrue()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.DeleteAsync(7, TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Delete);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/widgets/7");
    }

    // == Failure mapping via ServiceExceptionHelper ==
    [Fact]
    public async Task SendRequest_WithDomainExceptionPayload_ThrowsDomainInvariantViolation()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.BadRequest,
            """{"title":"Domain Exception","detail":"Widget is already retired."}"""));

        var act = () => sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DomainInvariantViolationException>()
            .WithMessage("Widget is already retired.");
    }

    [Fact]
    public async Task SendRequest_WithValidationExceptionPayload_JoinsErrorMessages()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.BadRequest,
            """{"title":"Validation Exception","errors":{"Name":["Name is required.","Name is too long."]}}"""));

        var act = () => sut.AddAsync(Widget(0), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DomainInvariantViolationException>()
            .WithMessage("Name is required. Name is too long.");
    }

    [Fact]
    public async Task SendRequest_WithUnrecognizedFailure_ThrowsHttpRequestException()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.BadRequest,
            """{"title":"Unknown Error","detail":"Something else."}"""));

        var act = () => sut.DeleteAsync(7, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // == Anonymous fallback ==
    [Fact]
    public async Task SendRequest_WithNoStoredToken_SendsAnonymousRequest()
    {
        var (sut, mocks) = CreateSut(_ => PagedResponse(0), token: null);

        await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Authorization.Should().BeNull();
    }
}
