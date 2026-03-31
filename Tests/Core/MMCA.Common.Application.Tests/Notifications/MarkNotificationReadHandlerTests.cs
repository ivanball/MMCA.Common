using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkRead;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Notifications;

public sealed class MarkNotificationReadHandlerTests
{
    // ── Mark read ──
    [Fact]
    public async Task HandleAsync_WhenNotificationExists_MarksAsReadAndSaves()
    {
        var (sut, mocks) = CreateSut(notificationExists: true);

        var command = new MarkNotificationReadCommand(NotificationId: 1, UserId: 42);
        Result result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Notification not found ──
    [Fact]
    public async Task HandleAsync_WhenNotificationNotFound_ReturnsNotFoundFailure()
    {
        var (sut, mocks) = CreateSut(notificationExists: false);

        var command = new MarkNotificationReadCommand(NotificationId: 999, UserId: 42);
        Result result = await sut.HandleAsync(command);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "UserNotification.NotFound");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Wrong user (no match) ──
    [Fact]
    public async Task HandleAsync_WhenWrongUser_ReturnsNotFoundFailure()
    {
        var (sut, _) = CreateSut(notificationExists: false);

        var command = new MarkNotificationReadCommand(NotificationId: 1, UserId: 999);
        Result result = await sut.HandleAsync(command);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "UserNotification.NotFound");
    }

    // ── Helpers ──
    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IQueryableExecutor> QueryableExecutor);

    private static (MarkNotificationReadHandler Sut, HandlerMocks Mocks) CreateSut(bool notificationExists)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<UserNotification, UserNotificationIdentifierType>>();
        var queryableExecutor = new Mock<IQueryableExecutor>();

        unitOfWork.Setup(x => x.GetRepository<UserNotification, UserNotificationIdentifierType>())
            .Returns(repository.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        repository.Setup(x => x.Table)
            .Returns(Enumerable.Empty<UserNotification>().AsQueryable());

        var matches = new List<UserNotification>();
        if (notificationExists)
        {
            var notification = UserNotification.Create(userId: 42, pushNotificationId: 100);
            matches.Add(notification);
        }

        queryableExecutor.Setup(x => x.ToListAsync(It.IsAny<IQueryable<UserNotification>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(matches);

        var sut = new MarkNotificationReadHandler(unitOfWork.Object, queryableExecutor.Object);
        var mocks = new HandlerMocks(unitOfWork, queryableExecutor);

        return (sut, mocks);
    }
}
