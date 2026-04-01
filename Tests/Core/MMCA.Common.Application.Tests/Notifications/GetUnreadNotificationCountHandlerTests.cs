using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetUnreadCount;
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

    // ── Helpers ──
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
