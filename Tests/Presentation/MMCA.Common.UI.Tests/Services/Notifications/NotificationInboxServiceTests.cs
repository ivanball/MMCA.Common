using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Exceptions;
using MMCA.Common.Shared.Notifications.UserNotifications;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Notifications;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Notifications;

/// <summary>
/// Verifies <see cref="NotificationInboxService"/>: the inbox REST contract (paged GET, unread-count
/// GET, per-item and bulk mark-read PUTs) on the named APIClient with the stored bearer token, plus
/// the failure shapes (domain ProblemDetails re-thrown, unread-count degrading to zero).
/// Failure-path responses use 4xx codes only; 5xx would engage the class-level Polly retry backoff.
/// </summary>
public sealed class NotificationInboxServiceTests
{
    private sealed record Mocks(StubHttpMessageHandler Handler, StubHttpClientFactory Factory);

    /// <summary>Scope provider stub: whatever the app currently scopes to, or null for unscoped.</summary>
    private sealed class StubScopeProvider(string? scopeKey) : INotificationScopeProvider
    {
        public Task<string?> GetCurrentScopeKeyAsync(CancellationToken ct = default) => Task.FromResult(scopeKey);
    }

    private static (NotificationInboxService Sut, Mocks Mocks) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? scopeKey = null)
    {
        var handler = new StubHttpMessageHandler(responder);
        var factory = new StubHttpClientFactory(handler);
        var tokenStorage = new Mock<ITokenStorageService>();
        tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync("stored-access-token");
        return (
            new NotificationInboxService(factory, tokenStorage.Object, new StubScopeProvider(scopeKey)),
            new Mocks(handler, factory));
    }

    private static UserNotificationDTO Notification(int id, bool isRead = false) => new()
    {
        Id = id,
        PushNotificationId = id + 100,
        Title = string.Create(CultureInfo.InvariantCulture, $"Title {id}"),
        Body = string.Create(CultureInfo.InvariantCulture, $"Body {id}"),
        IsRead = isRead,
    };

    private static HttpResponseMessage InboxResponse(params UserNotificationDTO[] items) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                new PagedCollectionResult<UserNotificationDTO>(items, new PaginationMetadata(37, 5, 2))),
        };

    private const string DomainErrorJson =
        """{"title":"Domain Exception","detail":"Notification is already read."}""";

    // == GetInboxAsync ==
    [Fact]
    public async Task GetInboxAsync_RequestsPagedInboxWithBearerToken_AndDeserializes()
    {
        var (sut, mocks) = CreateSut(_ => InboxResponse(Notification(1), Notification(2, isRead: true)));

        var result = await sut.GetInboxAsync(pageNumber: 2, pageSize: 5, TestContext.Current.CancellationToken);

        mocks.Factory.LastClientName.Should().Be("APIClient");
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Get);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/notifications/inbox?pageNumber=2&pageSize=5");
        mocks.Handler.LastRequest.Authorization!.ToString().Should().Be("Bearer stored-access-token");
        result.Should().NotBeNull();
        result!.Items.Should().HaveCount(2);
        result.PaginationMetadata.TotalItemCount.Should().Be(37);
    }

    [Fact]
    public async Task GetInboxAsync_WithDomainErrorPayload_ThrowsDomainInvariantViolation()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.BadRequest, DomainErrorJson));

        var act = () => sut.GetInboxAsync(cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DomainInvariantViolationException>()
            .WithMessage("Notification is already read.");
    }

    [Fact]
    public async Task GetInboxAsync_WithUnrecognizedFailure_ThrowsHttpRequestException()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => sut.GetInboxAsync(cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // == GetUnreadCountAsync ==
    [Fact]
    public async Task GetUnreadCountAsync_RequestsUnreadCountEndpoint_ReturnsCount()
    {
        var (sut, mocks) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "7"));

        var result = await sut.GetUnreadCountAsync(TestContext.Current.CancellationToken);

        result.Should().Be(7);
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Get);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/notifications/inbox/unread-count");
    }

    [Fact]
    public async Task GetUnreadCountAsync_OnFailure_ReturnsZeroWithoutThrowing()
    {
        // The badge must never break the page; any failure degrades to "no unread notifications".
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await sut.GetUnreadCountAsync(TestContext.Current.CancellationToken);

        result.Should().Be(0);
    }

    // == MarkReadAsync ==
    [Fact]
    public async Task MarkReadAsync_PutsToPerItemReadEndpoint()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await sut.MarkReadAsync(42, TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Put);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/notifications/inbox/42/read");
    }

    [Fact]
    public async Task MarkReadAsync_WithDomainErrorPayload_ThrowsDomainInvariantViolation()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.BadRequest, DomainErrorJson));

        var act = () => sut.MarkReadAsync(42, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DomainInvariantViolationException>();
    }

    // == MarkAllReadAsync ==
    [Fact]
    public async Task MarkAllReadAsync_PutsToReadAllEndpoint()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await sut.MarkAllReadAsync(TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Put);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/notifications/inbox/read-all");
    }

    // == Scope key ==
    // The provider decides the scope; an unscoped app must produce the byte-identical legacy URLs
    // pinned by the tests above, which is why those tests deliberately assert no scope parameter.
    [Fact]
    public async Task GetInboxAsync_WhenAppIsScoped_AppendsScopeToTheQuery()
    {
        var (sut, mocks) = CreateSut(_ => InboxResponse(Notification(1)), scopeKey: "event:2");

        await sut.GetInboxAsync(pageNumber: 2, pageSize: 5, TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should()
            .Be("/notifications/inbox?pageNumber=2&pageSize=5&scope=event%3A2");
    }

    [Fact]
    public async Task GetUnreadCountAsync_WhenAppIsScoped_AppendsScopeToTheQuery()
    {
        var (sut, mocks) = CreateSut(
            _ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "3"),
            scopeKey: "event:2");

        await sut.GetUnreadCountAsync(TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should()
            .Be("/notifications/inbox/unread-count?scope=event%3A2");
    }

    [Fact]
    public async Task MarkAllReadAsync_WhenAppIsScoped_AppendsScopeToTheQuery()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent), scopeKey: "event:2");

        await sut.MarkAllReadAsync(TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should()
            .Be("/notifications/inbox/read-all?scope=event%3A2");
    }

    [Fact]
    public async Task MarkReadAsync_WhenAppIsScoped_StaysUnscoped()
    {
        // A single mark-read targets one identifier the user already saw, so it needs no filter.
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent), scopeKey: "event:2");

        await sut.MarkReadAsync(42, TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/notifications/inbox/42/read");
    }

    [Fact]
    public async Task GetInboxAsync_WhenProviderReturnsWhitespace_OmitsTheScopeParameter()
    {
        var (sut, mocks) = CreateSut(_ => InboxResponse(Notification(1)), scopeKey: "   ");

        await sut.GetInboxAsync(cancellationToken: TestContext.Current.CancellationToken);

        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/notifications/inbox?pageNumber=1&pageSize=20");
    }
}
