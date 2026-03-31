using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetInbox;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Notifications.UserNotifications;
using Moq;

namespace MMCA.Common.Application.Tests.Notifications;

public sealed class GetMyNotificationsHandlerTests
{
    // ── Pagination with results ──
    [Fact]
    public async Task HandleAsync_WithNotifications_ReturnsPagedCollection()
    {
        var (sut, _) = CreateSut(totalCount: 5, pageItems: 5);

        var query = new GetMyNotificationsQuery(UserId: 1, PageNumber: 1, PageSize: 20);
        Result<PagedCollectionResult<UserNotificationDTO>> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().HaveCount(5);
        result.Value.PaginationMetadata.TotalItemCount.Should().Be(5);
    }

    // ── Empty results ──
    [Fact]
    public async Task HandleAsync_WhenNoNotifications_ReturnsEmptyCollection()
    {
        var (sut, _) = CreateSut(totalCount: 0, pageItems: 0);

        var query = new GetMyNotificationsQuery(UserId: 1, PageNumber: 1, PageSize: 20);
        Result<PagedCollectionResult<UserNotificationDTO>> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().BeEmpty();
        result.Value.PaginationMetadata.TotalItemCount.Should().Be(0);
    }

    // ── Page size capping ──
    [Fact]
    public async Task HandleAsync_WhenPageSizeExceedsMax_CapsAt500()
    {
        var (sut, _) = CreateSut(totalCount: 0, pageItems: 0);

        var query = new GetMyNotificationsQuery(UserId: 1, PageNumber: 1, PageSize: 999);
        Result<PagedCollectionResult<UserNotificationDTO>> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PaginationMetadata.PageSize.Should().Be(500);
    }

    // ── Helpers ──
    private static (GetMyNotificationsHandler Sut, Mock<IUnitOfWork> UnitOfWork) CreateSut(
        int totalCount, int pageItems)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var userNotificationRepo = new Mock<IRepository<UserNotification, UserNotificationIdentifierType>>();
        var pushNotificationRepo = new Mock<IRepository<PushNotification, PushNotificationIdentifierType>>();
        var queryableExecutor = new Mock<IQueryableExecutor>();

        unitOfWork.Setup(x => x.GetRepository<UserNotification, UserNotificationIdentifierType>())
            .Returns(userNotificationRepo.Object);
        unitOfWork.Setup(x => x.GetRepository<PushNotification, PushNotificationIdentifierType>())
            .Returns(pushNotificationRepo.Object);

        userNotificationRepo.Setup(x => x.TableNoTracking)
            .Returns(Enumerable.Empty<UserNotification>().AsQueryable());
        pushNotificationRepo.Setup(x => x.TableNoTracking)
            .Returns(Enumerable.Empty<PushNotification>().AsQueryable());

        List<UserNotificationDTO> dtos = [];
        for (int i = 0; i < pageItems; i++)
        {
            dtos.Add(new UserNotificationDTO
            {
                Id = i + 1,
                PushNotificationId = i + 1,
                Title = $"Title {i}",
                Body = $"Body {i}",
                IsRead = false,
                SentOn = DateTime.UtcNow,
            });
        }

        queryableExecutor.Setup(x => x.CountAsync(It.IsAny<IQueryable<UserNotificationDTO>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(totalCount);
        queryableExecutor.Setup(x => x.ToListAsync(It.IsAny<IQueryable<UserNotificationDTO>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dtos);

        var sut = new GetMyNotificationsHandler(unitOfWork.Object, queryableExecutor.Object);

        return (sut, unitOfWork);
    }
}
