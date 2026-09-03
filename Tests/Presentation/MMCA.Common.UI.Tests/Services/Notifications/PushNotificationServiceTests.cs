using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Notifications;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Notifications;

/// <summary>
/// Verifies <see cref="PushNotificationService"/>'s two service-specific operations on top of
/// <c>EntityServiceBase</c>: send (POST to <c>notifications</c>, created DTO required back) and
/// paginated history (GET with page parameters). Both answer with a <see cref="Result{T}"/>, so a
/// rejected send reaches the caller as the API's own errors instead of an exception. The inherited
/// CRUD contract is pinned separately in <c>EntityServiceBaseTests</c>.
/// </summary>
public sealed class PushNotificationServiceTests
{
    private sealed record Mocks(StubHttpMessageHandler Handler, StubHttpClientFactory Factory);

    /// <summary>Scope provider stub: whatever the app currently scopes to, or null for unscoped.</summary>
    private sealed class StubScopeProvider(string? scopeKey) : INotificationScopeProvider
    {
        public Task<string?> GetCurrentScopeKeyAsync(CancellationToken ct = default) => Task.FromResult(scopeKey);
    }

    private static (PushNotificationService Sut, Mocks Mocks) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? scopeKey = null)
    {
        var handler = new StubHttpMessageHandler(responder);
        var factory = new StubHttpClientFactory(handler);
        var tokenStorage = new Mock<ITokenStorageService>();
        tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync("stored-access-token");
        return (
            new PushNotificationService(factory, tokenStorage.Object, new StubScopeProvider(scopeKey)),
            new Mocks(handler, factory));
    }

    private static PushNotificationDTO Sent(int id, string title = "Maintenance window") => new()
    {
        Id = id,
        Title = title,
        Body = "The site goes down at midnight.",
        SentByUserId = 1,
        RecipientCount = 25,
        Status = "Sent",
    };

    [Fact]
    public async Task SendAsync_PostsRequestToNotifications_ReturnsCreatedDto()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(Sent(7)),
        });

        var result = await sut.SendAsync(
            new SendPushNotificationRequest("Maintenance window", "The site goes down at midnight."),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(7);
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/notifications");
        mocks.Handler.LastRequest.Body.Should().Contain("Maintenance window");
        mocks.Handler.LastRequest.Authorization!.ToString().Should().Be("Bearer stored-access-token");
    }

    [Fact]
    public async Task SendAsync_WhenApiReturnsNullBody_FailsAsEmptyResponse()
    {
        // A send that answers 200 without the created notification leaves the admin page with
        // nothing to show, so it is reported rather than passed off as a success.
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "null"));

        var result = await sut.SendAsync(
            new SendPushNotificationRequest("Title", "Body"),
            TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(ProblemDetailsResultReader.EmptyResponseCode);
    }

    [Fact]
    public async Task SendAsync_WhenServerRejects_CarriesTheServersErrors()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.BadRequest,
            """{"title":"Validation Exception","errors":{"Title":["Title is required."]}}"""));

        var result = await sut.SendAsync(
            new SendPushNotificationRequest(string.Empty, "Body"),
            TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Validation.Title");
        error.Target.Should().Be("Title");
        error.Type.Should().Be(ErrorType.Validation);
    }

    // == Scope stamping ==
    [Fact]
    public async Task SendAsync_WhenAppIsScoped_StampsScopeKeyOnTheRequestBody()
    {
        var (sut, mocks) = CreateSut(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Sent(7)) },
            scopeKey: "event:2");

        await sut.SendAsync(
            new SendPushNotificationRequest("Maintenance window", "The site goes down at midnight."),
            TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Body.Should().Contain("\"scopeKey\":\"event:2\"");
    }

    [Fact]
    public async Task SendAsync_WhenAppIsUnscoped_SendsNullScopeKey()
    {
        var (sut, mocks) = CreateSut(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Sent(7)) });

        await sut.SendAsync(
            new SendPushNotificationRequest("Maintenance window", "The site goes down at midnight."),
            TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Body.Should().Contain("\"scopeKey\":null");
    }

    [Fact]
    public async Task SendAsync_WhenRequestAlreadyCarriesAScope_KeepsTheCallersScope()
    {
        // An explicit caller choice outranks the ambient one.
        var (sut, mocks) = CreateSut(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Sent(7)) },
            scopeKey: "event:2");

        await sut.SendAsync(
            new SendPushNotificationRequest("Maintenance window", "The site goes down at midnight.")
            {
                ScopeKey = "event:9",
            },
            TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Body.Should().Contain("\"scopeKey\":\"event:9\"");
    }

    [Fact]
    public async Task GetHistoryAsync_RequestsPagedHistory_ReturnsWrapper()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PagedCollectionResult<PushNotificationDTO>(
                [Sent(1), Sent(2, "Second")], new PaginationMetadata(12, 25, 3))),
        });

        var result = await sut.GetHistoryAsync(pageNumber: 3, pageSize: 25, TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Get);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/notifications?pageNumber=3&pageSize=25");
        result.IsSuccess.Should().BeTrue();
        var page = result.Value!;
        page.Items.Should().HaveCount(2);
        page.PaginationMetadata.TotalItemCount.Should().Be(12);
    }
}
