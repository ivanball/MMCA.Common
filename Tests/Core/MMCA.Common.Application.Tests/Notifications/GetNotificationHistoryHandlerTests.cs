using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.GetHistory;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Notifications.PushNotifications;
using Moq;

namespace MMCA.Common.Application.Tests.Notifications;

public sealed class GetNotificationHistoryHandlerTests
{
    // ── Pagination ──
    [Fact]
    public async Task HandleAsync_WithResults_ReturnsPagedCollection()
    {
        var (sut, _) = CreateSut(totalCount: 25, pageItems: 10);

        var query = new GetNotificationHistoryQuery(PageNumber: 1, PageSize: 10);
        Result<PagedCollectionResult<PushNotificationDTO>> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().HaveCount(10);
        result.Value.PaginationMetadata.TotalItemCount.Should().Be(25);
    }

    // ── Empty results ──
    [Fact]
    public async Task HandleAsync_WhenNoNotifications_ReturnsEmptyCollection()
    {
        var (sut, _) = CreateSut(totalCount: 0, pageItems: 0);

        var query = new GetNotificationHistoryQuery(PageNumber: 1, PageSize: 10);
        Result<PagedCollectionResult<PushNotificationDTO>> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().BeEmpty();
        result.Value.PaginationMetadata.TotalItemCount.Should().Be(0);
    }

    // ── Max page size capping ──
    [Fact]
    public async Task HandleAsync_WhenPageSizeExceedsMax_CapsAt500()
    {
        var (sut, _) = CreateSut(totalCount: 1000, pageItems: 500);

        var query = new GetNotificationHistoryQuery(PageNumber: 1, PageSize: 999);
        Result<PagedCollectionResult<PushNotificationDTO>> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PaginationMetadata.PageSize.Should().Be(500);
    }

    // ── Helpers ──
    private static (GetNotificationHistoryHandler Sut, Mock<IUnitOfWork> UnitOfWork) CreateSut(
        int totalCount, int pageItems)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<PushNotification, PushNotificationIdentifierType>>();
        var queryableExecutor = new Mock<IQueryableExecutor>();

        unitOfWork.Setup(x => x.GetRepository<PushNotification, PushNotificationIdentifierType>())
            .Returns(repository.Object);

        repository.Setup(x => x.CountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(totalCount);

        // Create test notifications
        List<PushNotification> notifications = [];
        for (int i = 0; i < pageItems; i++)
        {
            Result<PushNotification> notificationResult = PushNotification.Create(
                $"Title {i}", $"Body {i}", sentByUserId: 1, recipientCount: 5);
            notifications.Add(notificationResult.Value!);
        }

        repository.Setup(x => x.TableNoTracking)
            .Returns(notifications.AsQueryable());

        queryableExecutor.Setup(x => x.ToListAsync(It.IsAny<IQueryable<PushNotification>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(notifications);

        var dtoMapper = new PushNotificationDTOMapper();
        var sut = new GetNotificationHistoryHandler(unitOfWork.Object, queryableExecutor.Object, dtoMapper);

        return (sut, unitOfWork);
    }
}
