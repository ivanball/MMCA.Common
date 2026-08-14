using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Notifications;

public sealed class MarkAllNotificationsReadHandlerTests
{
    // ── Marks all unread as read ──
    [Fact]
    public async Task HandleAsync_WithUnreadNotifications_MarksAllAsReadAndSaves()
    {
        var (sut, mocks) = CreateSut(unreadCount: 3);

        var command = new MarkAllNotificationsReadCommand(UserId: 42);
        Result result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── No unread notifications skips save ──
    [Fact]
    public async Task HandleAsync_WhenNoUnreadNotifications_SkipsSave()
    {
        var (sut, mocks) = CreateSut(unreadCount: 0);

        var command = new MarkAllNotificationsReadCommand(UserId: 42);
        Result result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Returns success even with no unread ──
    [Fact]
    public async Task HandleAsync_WhenNoUnreadNotifications_ReturnsSuccess()
    {
        var (sut, _) = CreateSut(unreadCount: 0);

        var command = new MarkAllNotificationsReadCommand(UserId: 42);
        Result result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
    }

    // ── Read time is stamped from the injected clock ──
    [Fact]
    public async Task HandleAsync_WithUnreadNotifications_StampsReadOnFromInjectedClock()
    {
        var readInstant = new DateTimeOffset(2026, 6, 26, 14, 30, 0, TimeSpan.Zero);
        var (sut, mocks) = CreateSut(unreadCount: 3, timeProvider: new FixedTimeProvider(readInstant));

        Result result = await sut.HandleAsync(new MarkAllNotificationsReadCommand(UserId: 42));

        result.IsSuccess.Should().BeTrue();
        mocks.Unread.Should().HaveCount(3);
        mocks.Unread.Should().OnlyContain(n => n.IsRead && n.ReadOn == readInstant.UtcDateTime);
    }

    // ── Scope filtering ──
    [Fact]
    public async Task HandleAsync_WithoutScope_NeverJoinsPushNotification()
    {
        // Mirrors the unread count: an unconditional join would drag PushNotification's soft-delete
        // global filter into the legacy command and silently change which rows it clears.
        var (sut, mocks) = CreateSut(unreadCount: 3);

        Result result = await sut.HandleAsync(new MarkAllNotificationsReadCommand(UserId: 42));

        result.IsSuccess.Should().BeTrue();
        mocks.UnitOfWork.Verify(x => x.GetRepository<PushNotification, PushNotificationIdentifierType>(), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WithScope_MarksMatchingAndUnscopedOnly()
    {
        var (sut, notifications) = CreateFilteringSut();

        Result result = await sut.HandleAsync(new MarkAllNotificationsReadCommand(UserId: 1, ScopeKey: "event:2"));

        result.IsSuccess.Should().BeTrue();
        notifications[0].IsRead.Should().BeTrue("the unscoped notification is visible under every scope");
        notifications[1].IsRead.Should().BeFalse("an \"event:1\" notification is invisible to an \"event:2\" client");
        notifications[2].IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithoutScope_MarksEveryUnreadNotification()
    {
        var (sut, notifications) = CreateFilteringSut();

        Result result = await sut.HandleAsync(new MarkAllNotificationsReadCommand(UserId: 1));

        result.IsSuccess.Should().BeTrue();
        notifications.Should().OnlyContain(n => n.IsRead);
    }

    // ── Helpers ──

    /// <summary>
    /// Builds a handler over three real unread notifications (unscoped, "event:1", "event:2") with an
    /// executor that actually enumerates the composed query, so these tests pin the scope predicate
    /// rather than a mocked result set. The returned list is in that same order.
    /// </summary>
    private static (MarkAllNotificationsReadHandler Sut, IReadOnlyList<UserNotification> Notifications) CreateFilteringSut()
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
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        repository.Setup(x => x.Table).Returns(userNotifications.AsQueryable());
        pushNotificationRepo.Setup(x => x.TableNoTracking).Returns(pushNotifications.AsQueryable());

        queryableExecutor.Setup(x => x.ToListAsync(It.IsAny<IQueryable<UserNotification>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IQueryable<UserNotification> source, CancellationToken _) => source.ToList());

        var sut = new MarkAllNotificationsReadHandler(unitOfWork.Object, queryableExecutor.Object, TimeProvider.System);

        return (sut, userNotifications);
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IQueryableExecutor> QueryableExecutor,
        IReadOnlyList<UserNotification> Unread);

    private static (MarkAllNotificationsReadHandler Sut, HandlerMocks Mocks) CreateSut(
        int unreadCount,
        TimeProvider? timeProvider = null)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<UserNotification, UserNotificationIdentifierType>>();
        var queryableExecutor = new Mock<IQueryableExecutor>();

        unitOfWork.Setup(x => x.GetRepository<UserNotification, UserNotificationIdentifierType>())
            .Returns(repository.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(unreadCount);

        repository.Setup(x => x.Table)
            .Returns(Enumerable.Empty<UserNotification>().AsQueryable());

        var unread = new List<UserNotification>();
        for (int i = 0; i < unreadCount; i++)
        {
            var notification = UserNotification.Create(userId: 42, pushNotificationId: i + 1).Value!;
            unread.Add(notification);
        }

        queryableExecutor.Setup(x => x.ToListAsync(It.IsAny<IQueryable<UserNotification>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(unread);

        var sut = new MarkAllNotificationsReadHandler(unitOfWork.Object, queryableExecutor.Object, timeProvider ?? TimeProvider.System);
        var mocks = new HandlerMocks(unitOfWork, queryableExecutor, unread);

        return (sut, mocks);
    }
}
