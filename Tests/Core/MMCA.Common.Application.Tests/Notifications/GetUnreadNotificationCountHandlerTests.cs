using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetUnreadCount;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Notifications;

public sealed class GetUnreadNotificationCountHandlerTests
{
    // ── Returns count of unread notifications ──
    [Fact]
    public async Task HandleAsync_WithUnreadNotifications_ReturnsCount()
    {
        var (sut, _) = CreateSut(unreadCount: 7);

        var query = new GetUnreadNotificationCountQuery(UserId: 42);
        Result<int> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(7);
    }

    // ── Returns zero when none unread ──
    [Fact]
    public async Task HandleAsync_WhenNoUnreadNotifications_ReturnsZero()
    {
        var (sut, _) = CreateSut(unreadCount: 0);

        var query = new GetUnreadNotificationCountQuery(UserId: 42);
        Result<int> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
    }

    // ── Always returns success ──
    [Fact]
    public async Task HandleAsync_Always_ReturnsSuccess()
    {
        var (sut, _) = CreateSut(unreadCount: 3);

        var query = new GetUnreadNotificationCountQuery(UserId: 1);
        Result<int> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
    }

    // ── Scope filtering ──
    [Fact]
    public async Task HandleAsync_WithoutScope_NeverJoinsPushNotification()
    {
        // The legacy count must stay the legacy query: joining PushNotification unconditionally
        // would pull its soft-delete global filter into a number nobody asked to change.
        var (sut, mocks) = CreateSut(unreadCount: 7);

        Result<int> result = await sut.HandleAsync(new GetUnreadNotificationCountQuery(UserId: 42));

        result.Value.Should().Be(7);
        mocks.UnitOfWork.Verify(x => x.GetRepository<PushNotification, PushNotificationIdentifierType>(), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WithScope_CountsMatchingAndUnscopedOnly()
    {
        GetUnreadNotificationCountHandler sut = CreateFilteringSut();

        Result<int> result = await sut.HandleAsync(new GetUnreadNotificationCountQuery(UserId: 1, ScopeKey: "event:2"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2);
    }

    [Fact]
    public async Task HandleAsync_WithoutScope_CountsEveryUnreadNotification()
    {
        GetUnreadNotificationCountHandler sut = CreateFilteringSut();

        Result<int> result = await sut.HandleAsync(new GetUnreadNotificationCountQuery(UserId: 1));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(3);
    }

    [Fact]
    public async Task HandleAsync_WithScopeMatchingNothing_CountsUnscopedOnly()
    {
        GetUnreadNotificationCountHandler sut = CreateFilteringSut();

        Result<int> result = await sut.HandleAsync(new GetUnreadNotificationCountQuery(UserId: 1, ScopeKey: "event:99"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
    }

    // ── Helpers ──

    /// <summary>
    /// Builds a handler over three real unread notifications (unscoped, "event:1", "event:2") with an
    /// executor that actually enumerates the composed query, so these tests pin the scope predicate
    /// rather than a mocked count.
    /// </summary>
    private static GetUnreadNotificationCountHandler CreateFilteringSut()
    {
        List<PushNotification> pushNotifications =
        [
            Push(id: 1, scopeKey: null),
            Push(id: 2, scopeKey: "event:1"),
            Push(id: 3, scopeKey: "event:2"),
        ];
        List<UserNotification> userNotifications =
            [.. pushNotifications.Select(pn => UserNotification.Create(userId: 1, pushNotificationId: pn.Id).Value!)];

        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<UserNotification, UserNotificationIdentifierType>>();
        var pushNotificationRepo = new Mock<IRepository<PushNotification, PushNotificationIdentifierType>>();
        var queryableExecutor = new Mock<IQueryableExecutor>();

        unitOfWork.Setup(x => x.GetRepository<UserNotification, UserNotificationIdentifierType>())
            .Returns(repository.Object);
        unitOfWork.Setup(x => x.GetRepository<PushNotification, PushNotificationIdentifierType>())
            .Returns(pushNotificationRepo.Object);

        repository.Setup(x => x.TableNoTracking).Returns(userNotifications.AsQueryable());
        pushNotificationRepo.Setup(x => x.TableNoTracking).Returns(pushNotifications.AsQueryable());

        queryableExecutor.Setup(x => x.CountAsync(It.IsAny<IQueryable<UserNotification>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IQueryable<UserNotification> source, CancellationToken _) => source.Count());

        return new GetUnreadNotificationCountHandler(unitOfWork.Object, queryableExecutor.Object);
    }

    /// <summary>
    /// Builds a notification that looks persisted. <c>Id</c> is <c>required init</c> and the factory
    /// leaves it at its default, so the identifier the join needs is written back through reflection
    /// rather than by opening the entity up with a test-only setter.
    /// </summary>
    private static PushNotification Push(PushNotificationIdentifierType id, string? scopeKey)
    {
        PushNotification notification = PushNotification
            .Create("Title", "Body", sentByUserId: 1, recipientCount: 1, scopeKey: scopeKey).Value!;
        typeof(PushNotification).GetProperty(nameof(PushNotification.Id))!.SetValue(notification, id);
        return notification;
    }

    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IQueryableExecutor> QueryableExecutor);

    private static (GetUnreadNotificationCountHandler Sut, HandlerMocks Mocks) CreateSut(int unreadCount)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<UserNotification, UserNotificationIdentifierType>>();
        var queryableExecutor = new Mock<IQueryableExecutor>();

        unitOfWork.Setup(x => x.GetRepository<UserNotification, UserNotificationIdentifierType>())
            .Returns(repository.Object);

        repository.Setup(x => x.TableNoTracking)
            .Returns(Enumerable.Empty<UserNotification>().AsQueryable());

        queryableExecutor.Setup(x => x.CountAsync(It.IsAny<IQueryable<UserNotification>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(unreadCount);

        var sut = new GetUnreadNotificationCountHandler(unitOfWork.Object, queryableExecutor.Object);
        var mocks = new HandlerMocks(unitOfWork, queryableExecutor);

        return (sut, mocks);
    }
}
