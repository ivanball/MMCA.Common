using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Pins the CRUD verb/route/serialization contract of <see cref="EntityServiceBase{TEntityDTO, TId}"/>
/// through a minimal concrete subclass: GET list (+flags), GET paged (+full query-string building),
/// GET lookup, GET by id (404 as a <see cref="ErrorType.NotFound"/> failure), POST create, PUT
/// update, DELETE, the named APIClient + bearer-token plumbing, and failure mapping through
/// <see cref="ProblemDetailsResultReader"/> (plus transport faults through
/// <see cref="HttpResultExecutor"/>).
/// <para>
/// Nothing here throws for a server answer: every outcome arrives as a <see cref="Result"/>, and the
/// only exception that still escapes is the caller's own cancellation. Failure responses use 4xx
/// codes only; 5xx would engage the class-level Polly retry backoff (2s/4s/8s).
/// </para>
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

    /// <summary>The Problem Details a domain-rule rejection arrives as: a title and a detail, no error array.</summary>
    private const string DomainErrorJson =
        """{"title":"Domain Exception","detail":"Widget is already retired."}""";

    /// <summary>The ASP.NET Core validation shape: an <c>errors</c> object of property to messages.</summary>
    private const string ValidationErrorJson =
        """{"title":"Validation Exception","errors":{"Name":["Name is required.","Name is too long."]}}""";

    /// <summary>The MMCA shape: an <c>errors</c> array that states its own code and type.</summary>
    private const string MmcaErrorArrayJson =
        """
        {"title":"Unprocessable Entity","errors":[{"code":"Widget.Retired","message":"A retired widget cannot be renamed.","type":"Invariant","target":"Name"}]}
        """;

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
        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(w => w.Id).Should().Equal(1, 2);
    }

    [Fact]
    public async Task GetAllAsync_WithIncludeFlagsSet_EncodesTrueValues()
    {
        var (sut, mocks) = CreateSut(_ => PagedResponse(0));

        await sut.GetAllAsync(includeFKs: true, includeChildren: true, TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/widgets?includeFKs=True&includeChildren=True");
    }

    [Fact]
    public async Task GetAllAsync_WhenBodyDeserializesToNull_FailsAsEmptyResponse()
    {
        // A list endpoint that answers 200 with no page at all is broken, not empty: reporting it as
        // an empty list rendered "no rows" over a failure the user could have retried.
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "null"));

        var result = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(ProblemDetailsResultReader.EmptyResponseCode);
    }

    [Fact]
    public async Task GetAllAsync_WhenServerAnswersUnauthorized_FailsAsUnauthorized()
    {
        // The status alone types the error when the payload does not; the page can branch on
        // ErrorType.Unauthorized to send the user back to sign-in.
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.401");
        error.Type.Should().Be(ErrorType.Unauthorized);
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

        var result = await sut.GetPagedAsync(
            filters, pageNumber: 2, pageSize: 25, sortColumn: "Name", sortDirection: "desc",
            includeChildren: false, TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be(
            "/widgets/paged?pageNumber=2&pageSize=25&sortColumn=Name&sortDirection=desc&includeChildren=False"
            + "&filters[Name].operator=contains&filters[Name].value=blue%20shirt");
        result.IsSuccess.Should().BeTrue();
        var (items, totalItems) = result.Value;
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
    public async Task GetPagedAsync_WhenBodyDeserializesToNull_FailsAsEmptyResponse()
    {
        // Same reasoning as the list read: a grid must not paint an empty page over a broken one.
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "null"));

        var result = await sut.GetPagedAsync(
            [], pageNumber: 1, pageSize: 10, sortColumn: null, sortDirection: null,
            includeChildren: false, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(ProblemDetailsResultReader.EmptyResponseCode);
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
        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().ContainSingle().Which.Name.Should().Be("First");
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
        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Blue");
    }

    [Fact]
    public async Task GetByIdAsync_OnNotFound_FailsAsNotFoundInsteadOfReturningNull()
    {
        // A missing entity used to arrive as the same null a transport failure produced. As a typed
        // failure the page can tell "this id does not exist" apart from "the call did not land".
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.GetByIdAsync(999, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.404");
        error.Type.Should().Be(ErrorType.NotFound);
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
        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(7);
    }

    [Fact]
    public async Task AddAsync_WhenApiReturnsNullBody_FailsAsEmptyResponse()
    {
        // A create that answers 200 without the created entity leaves the caller with no id to
        // navigate to, so it is a failure rather than a success carrying nothing.
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "null"));

        var result = await sut.AddAsync(Widget(0), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(ProblemDetailsResultReader.EmptyResponseCode);
    }

    // == UpdateAsync ==
    [Fact]
    public async Task UpdateAsync_PutsEntityToIdRoute_Succeeds()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.UpdateAsync(Widget(7, "Renamed"), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Put);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/widgets/7");
        mocks.Handler.LastRequest.Body.Should().Contain("Renamed");
    }

    [Fact]
    public async Task UpdateAsync_WhenServerRejects_CarriesTheServersErrors()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.BadRequest, ValidationErrorJson));

        var result = await sut.UpdateAsync(Widget(7, string.Empty), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().HaveCount(2);
        result.Errors.Select(e => e.Message).Should().Equal("Name is required.", "Name is too long.");
    }

    // == DeleteAsync ==
    [Fact]
    public async Task DeleteAsync_DeletesIdRoute_Succeeds()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.DeleteAsync(7, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Delete);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/widgets/7");
    }

    [Fact]
    public async Task DeleteAsync_OnNotFound_FailsAsNotFound()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.DeleteAsync(7, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.NotFound);
    }

    // == Failure mapping via ProblemDetailsResultReader ==
    [Fact]
    public async Task SendRequest_WithDomainExceptionPayload_FailsWithTheDetailAsMessage()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.BadRequest, DomainErrorJson));

        var result = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        // A plain ProblemDetails states no code of its own, so the status supplies one. 400 is the
        // lossy direction of the API's mapping (Validation, Invariant and Failure all reach the wire
        // as 400), and the reader resolves it to Validation.
        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.400");
        error.Message.Should().Be("Widget is already retired.");
        error.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public async Task SendRequest_WithValidationExceptionPayload_FailsOncePerMessage()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.BadRequest, ValidationErrorJson));

        var result = await sut.AddAsync(Widget(0), TestContext.Current.CancellationToken);

        // The messages are no longer joined into one string: each stays its own error, keyed by the
        // property it belongs to, so a form can put each message on its own field.
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().HaveCount(2);
        result.Errors.Should().AllSatisfy(error =>
        {
            error.Code.Should().Be("Validation.Name");
            error.Target.Should().Be("Name");
            error.Type.Should().Be(ErrorType.Validation);
        });
        result.Errors.Select(e => e.Message).Should().Equal("Name is required.", "Name is too long.");
    }

    [Fact]
    public async Task SendRequest_WithMmcaErrorArray_RoundTripsCodeMessageAndType()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.UnprocessableEntity, MmcaErrorArrayJson));

        var result = await sut.UpdateAsync(Widget(7, "Renamed"), TestContext.Current.CancellationToken);

        // The one lossless shape: the payload states its own type, so Invariant survives even though
        // the status alone would have said UnprocessableEntity.
        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Widget.Retired");
        error.Message.Should().Be("A retired widget cannot be renamed.");
        error.Type.Should().Be(ErrorType.Invariant);
        error.Target.Should().Be("Name");
    }

    [Fact]
    public async Task SendRequest_WithUnrecognizedFailure_FailsWithTheStatusCode()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.BadRequest,
            """{"title":"Unknown Error","detail":"Something else."}"""));

        var result = await sut.DeleteAsync(7, TestContext.Current.CancellationToken);

        // Nothing recognizable in the payload is no longer a bare transport exception: the status
        // still yields one usable error the page can render.
        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.400");
        error.Message.Should().Be("Something else.");
    }

    [Fact]
    public async Task SendRequest_WithNonJsonBody_StillFailsWithTheStatusCode()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.Forbidden, "<html><body>Blocked by the proxy</body></html>"));

        var result = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        // An HTML error page from a proxy never parses, and the reader must still hand back exactly
        // one error rather than an empty failure (which would read as a success).
        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.403");
        error.Type.Should().Be(ErrorType.Forbidden);
    }

    // == SendRequestAsync overloads: body expected vs no body expected ==
    [Fact]
    public async Task SendRequest_GenericOverload_OnBodylessSuccess_FailsAsEmptyResponse()
    {
        // GetByIdAsync asked for a value; a 204 does not carry one, so the caller is told rather
        // than handed a default-valued DTO.
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.GetByIdAsync(7, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(ProblemDetailsResultReader.EmptyResponseCode);
        error.Type.Should().Be(ErrorType.Unexpected);
    }

    [Fact]
    public async Task SendRequest_NonGenericOverload_OnBodylessSuccess_Succeeds()
    {
        // The same 204 through the overload that asks for no value is the normal answer to a PUT.
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.UpdateAsync(Widget(7, "Renamed"), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // == Transport faults ==
    [Fact]
    public async Task SendRequest_WhenTheSocketDrops_FailsAsATransportError()
    {
        // No response ever arrives, so there is no Problem Details to read: HttpResultExecutor is
        // the half of the pipeline that keeps the method honestly typed as returning a Result.
        var (sut, _) = CreateSut(_ => throw new IOException("The connection was reset."));

        var result = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(HttpResultExecutor.TransportErrorCode);
        error.Type.Should().Be(ErrorType.Unexpected);
        error.Source.Should().Be(
            "The connection was reset.", "the raw exception text is diagnostic detail, not a rendered message");
    }

    // == Cancellation ==
    [Fact]
    public async Task SendRequest_WhenTheCallerCancels_StillThrowsOperationCanceled()
    {
        // A page owns its own cancellation (a disposed component, a superseded grid fetch). Turning
        // that into a rendered failure would make every abandoned navigation look like an outage.
        var (sut, _) = CreateSut(_ => PagedResponse(0));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => sut.GetAllAsync(cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
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
