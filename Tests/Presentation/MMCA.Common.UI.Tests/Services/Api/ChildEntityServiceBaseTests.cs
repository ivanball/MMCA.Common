using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Api;

/// <summary>
/// Pins the join-entity contract of <see cref="ChildEntityServiceBase"/> through a minimal concrete
/// subclass: POST of the join payload to the configured endpoint (both the overload that reads the
/// created DTO back and the one for a 204 endpoint), DELETE by id under it (404 arrives as a
/// <see cref="ErrorType.NotFound"/> failure rather than <see langword="false"/>), the named
/// APIClient, the Bearer-token plumbing via <see cref="AuthenticatedServiceBase"/>, and domain-error
/// extraction via <see cref="MMCA.Common.Shared.Http.ProblemDetailsResultReader"/>.
/// </summary>
public sealed class ChildEntityServiceBaseTests
{
    private sealed record MembershipDto
    {
        public required int Id { get; init; }

        public int MemberId { get; init; }
    }

    private sealed class MembershipService(IHttpClientFactory httpClientFactory, ITokenStorageService tokenStorageService)
        : ChildEntityServiceBase(httpClientFactory, tokenStorageService, "teams/5/members")
    {
        /// <summary>The endpoint that answers with the created join row.</summary>
        public Task<Result<MembershipDto>> AddAsync(object request, CancellationToken cancellationToken) =>
            PostAsync<MembershipDto>(request, cancellationToken);

        /// <summary>The endpoint that answers 204, so no body is asked for.</summary>
        public Task<Result> AddWithoutResponseAsync(object request, CancellationToken cancellationToken) =>
            PostAsync(request, cancellationToken);

        public Task<Result> RemoveAsync(string id, CancellationToken cancellationToken) =>
            DeleteByIdAsync(id, cancellationToken);
    }

    private sealed record Mocks(StubHttpMessageHandler Handler, StubHttpClientFactory Factory);

    private const string DomainErrorJson =
        """{"title":"Domain Exception","detail":"Member already belongs to the team."}""";

    private const string ValidationErrorJson =
        """{"title":"Validation Exception","errors":{"MemberId":["The member does not exist."]}}""";

    private static (MembershipService Sut, Mocks Mocks) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? token = "stored-access-token")
    {
        var handler = new StubHttpMessageHandler(responder);
        var factory = new StubHttpClientFactory(handler);
        var tokenStorage = new Mock<ITokenStorageService>();
        tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(token);
        return (new MembershipService(factory, tokenStorage.Object), new Mocks(handler, factory));
    }

    private static HttpResponseMessage CreatedMembership() =>
        new(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new MembershipDto { Id = 9, MemberId = 42 }),
        };

    // == PostAsync with a response body ==
    [Fact]
    public async Task PostAsync_PostsJoinPayloadToEndpoint_ReturnsCreatedDto()
    {
        var (sut, mocks) = CreateSut(_ => CreatedMembership());

        var result = await sut.AddAsync(new { TeamId = 5, MemberId = 42 }, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(9);
        mocks.Factory.LastClientName.Should().Be("APIClient");
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/teams/5/members");
        mocks.Handler.LastRequest.Body.Should().Contain("42");
        mocks.Handler.LastRequest.Authorization!.ToString().Should().Be("Bearer stored-access-token");
    }

    // == PostAsync without a response body ==
    [Fact]
    public async Task PostAsyncWithoutResponse_OnNoContent_Succeeds()
    {
        // A join endpoint that answers 204 is not a failure: the overload that asks for no value is
        // the one to use there, and the generic overload would report an empty body instead.
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.AddWithoutResponseAsync(new { MemberId = 42 }, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/teams/5/members");
    }

    // == Bearer-token plumbing ==
    [Fact]
    public async Task PostAsyncAndDeleteByIdAsync_AttachBearerTokenToEveryRequest()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await sut.AddWithoutResponseAsync(new { MemberId = 42 }, TestContext.Current.CancellationToken);
        await sut.RemoveAsync("42", TestContext.Current.CancellationToken);

        mocks.Handler.Requests.Should().HaveCount(2);
        mocks.Handler.Requests.Should().AllSatisfy(r =>
            r.Authorization!.ToString().Should().Be("Bearer stored-access-token"));
    }

    [Fact]
    public async Task PostAsync_WithNoStoredToken_SendsAnonymousRequest()
    {
        var (sut, mocks) = CreateSut(_ => CreatedMembership(), token: null);

        var result = await sut.AddAsync(new { MemberId = 42 }, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        mocks.Handler.LastRequest.Authorization.Should().BeNull();
    }

    // == Failure mapping ==
    [Fact]
    public async Task PostAsync_WithDomainErrorPayload_FailsWithTheDetailAsMessage()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.BadRequest, DomainErrorJson));

        var result = await sut.AddAsync(new { MemberId = 42 }, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.400");
        error.Message.Should().Be("Member already belongs to the team.");
        error.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public async Task PostAsync_WithValidationErrors_FailsOncePerMessageKeyedByProperty()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.BadRequest, ValidationErrorJson));

        var result = await sut.AddAsync(new { MemberId = 42 }, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Validation.MemberId");
        error.Target.Should().Be("MemberId");
        error.Message.Should().Be("The member does not exist.");
    }

    [Fact]
    public async Task PostAsync_WithUnrecognizedFailure_FailsWithTheStatusCode()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Conflict));

        var result = await sut.AddAsync(new { MemberId = 42 }, TestContext.Current.CancellationToken);

        // A body-less 409 still types itself: the status is the only thing the reader has, and it is
        // enough for the page to say the join already exists.
        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.409");
        error.Type.Should().Be(ErrorType.Conflict);
    }

    // == DeleteByIdAsync ==
    [Fact]
    public async Task DeleteByIdAsync_DeletesChildRoute_Succeeds()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.RemoveAsync("42", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Delete);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/teams/5/members/42");
    }

    [Fact]
    public async Task DeleteByIdAsync_OnNotFound_FailsAsNotFound()
    {
        // The old contract collapsed "there was nothing to remove" and "the remove failed" into one
        // false. A typed NotFound failure keeps the two apart at the call site.
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.RemoveAsync("42", TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.404");
        error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task DeleteByIdAsync_WithDomainErrorPayload_ReturnsTheDescribedFailure()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.BadRequest, DomainErrorJson));

        var result = await sut.RemoveAsync("42", TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Message.Should().Be("Member already belongs to the team.");
    }
}
