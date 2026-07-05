#pragma warning disable CA2000 // Dispose objects before losing scope - test doubles do not hold real resources
#pragma warning disable CA2025 // Task captures IDisposable - responses are awaited inline in tests

using System.Net;
using AwesomeAssertions;
using MMCA.Common.Shared.Exceptions;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Tests.Infrastructure;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Pins the join-entity contract of <see cref="ChildEntityServiceBase"/> through a minimal concrete
/// subclass: POST of the join payload to the configured endpoint, DELETE by id under it (404 maps to
/// <see langword="false"/> instead of throwing), the named APIClient, and domain-error extraction via
/// <see cref="ServiceExceptionHelper"/>.
/// </summary>
public sealed class ChildEntityServiceBaseTests
{
    private sealed class MembershipService(IHttpClientFactory httpClientFactory)
        : ChildEntityServiceBase(httpClientFactory, "teams/5/members")
    {
        public Task<HttpResponseMessage> AddAsync<TRequest>(TRequest request, CancellationToken cancellationToken) =>
            PostAsync(request, cancellationToken);

        public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken) =>
            DeleteByIdAsync(id, cancellationToken);
    }

    private sealed record Mocks(StubHttpMessageHandler Handler, StubHttpClientFactory Factory);

    private static (MembershipService Sut, Mocks Mocks) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHttpMessageHandler(responder);
        var factory = new StubHttpClientFactory(handler);
        return (new MembershipService(factory), new Mocks(handler, factory));
    }

    private const string DomainErrorJson =
        """{"title":"Domain Exception","detail":"Member already belongs to the team."}""";

    // == PostAsync ==
    [Fact]
    public async Task PostAsync_PostsJoinPayloadToEndpoint_ReturnsResponse()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Created));

        using var response = await sut.AddAsync(
            new { TeamId = 5, MemberId = 42 }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        mocks.Factory.LastClientName.Should().Be("APIClient");
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/teams/5/members");
        mocks.Handler.LastRequest.Body.Should().Contain("42");
    }

    [Fact]
    public async Task PostAsync_WithDomainErrorPayload_ThrowsDomainInvariantViolation()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.BadRequest, DomainErrorJson));

        var act = () => sut.AddAsync(new { MemberId = 42 }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DomainInvariantViolationException>()
            .WithMessage("Member already belongs to the team.");
    }

    [Fact]
    public async Task PostAsync_WithUnrecognizedFailure_ThrowsHttpRequestException()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Conflict));

        var act = () => sut.AddAsync(new { MemberId = 42 }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // == DeleteByIdAsync ==
    [Fact]
    public async Task DeleteByIdAsync_DeletesChildRoute_ReturnsTrue()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.RemoveAsync("42", TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Delete);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/teams/5/members/42");
    }

    [Fact]
    public async Task DeleteByIdAsync_OnNotFound_ReturnsFalseWithoutThrowing()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.RemoveAsync("42", TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteByIdAsync_WithDomainErrorPayload_ThrowsDomainInvariantViolation()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.BadRequest, DomainErrorJson));

        var act = () => sut.RemoveAsync("42", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DomainInvariantViolationException>();
    }
}
