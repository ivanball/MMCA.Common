using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;
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

    // ── Helpers ──
    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IQueryableExecutor> QueryableExecutor);

    private static (MarkAllNotificationsReadHandler Sut, HandlerMocks Mocks) CreateSut(int unreadCount)
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
            var notification = UserNotification.Create(userId: 42, pushNotificationId: i + 1);
            unread.Add(notification);
        }

        queryableExecutor.Setup(x => x.ToListAsync(It.IsAny<IQueryable<UserNotification>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(unread);

        var sut = new MarkAllNotificationsReadHandler(unitOfWork.Object, queryableExecutor.Object);
        var mocks = new HandlerMocks(unitOfWork, queryableExecutor);

        return (sut, mocks);
    }
}
