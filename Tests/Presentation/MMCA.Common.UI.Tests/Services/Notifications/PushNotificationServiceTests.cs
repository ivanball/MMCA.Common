using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Notifications;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Notifications;

/// <summary>
/// Verifies <see cref="PushNotificationService"/>'s two service-specific operations on top of
/// <c>EntityServiceBase</c>: send (POST to <c>notifications</c>, created DTO required back) and
/// paginated history (GET with page parameters). The inherited CRUD contract is pinned separately
/// in <c>EntityServiceBaseTests</c>.
/// </summary>
public sealed class PushNotificationServiceTests
{
    private sealed record Mocks(StubHttpMessageHandler Handler, StubHttpClientFactory Factory);

    private static (PushNotificationService Sut, Mocks Mocks) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHttpMessageHandler(responder);
        var factory = new StubHttpClientFactory(handler);
        var tokenStorage = new Mock<ITokenStorageService>();
        tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync("stored-access-token");
        return (new PushNotificationService(factory, tokenStorage.Object), new Mocks(handler, factory));
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

        result.Should().NotBeNull();
        result!.Id.Should().Be(7);
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        mocks.Handler.LastRequest.Uri!.PathAndQuery.Should().Be("/notifications");
        mocks.Handler.LastRequest.Body.Should().Contain("Maintenance window");
        mocks.Handler.LastRequest.Authorization!.ToString().Should().Be("Bearer stored-access-token");
    }

    [Fact]
    public async Task SendAsync_WhenApiReturnsNullBody_ThrowsInvalidOperation()
    {
        var (sut, _) = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "null"));

        var act = () => sut.SendAsync(
            new SendPushNotificationRequest("Title", "Body"),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
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
        result.Should().NotBeNull();
        result!.Items.Should().HaveCount(2);
        result.PaginationMetadata.TotalItemCount.Should().Be(12);
    }
}
