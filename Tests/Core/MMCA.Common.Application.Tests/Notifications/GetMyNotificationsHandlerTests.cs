using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetInbox;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;
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

    // ── Sub-1 page numbers ──
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task HandleAsync_WithNegativePageNumber_ReportsPageOneWithoutThrowing(int pageNumber)
    {
        var (sut, _) = CreateSut(totalCount: 5, pageItems: 5);

        var query = new GetMyNotificationsQuery(UserId: 1, pageNumber, PageSize: 20);
        Result<PagedCollectionResult<UserNotificationDTO>> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PaginationMetadata.CurrentPage.Should().Be(1);
        result.Value.PaginationMetadata.PageSize.Should().Be(20);
    }

    // ── Scope filtering ──
    [Fact]
    public async Task HandleAsync_WithoutScope_ReturnsEveryNotificationWhateverItsScope()
    {
        GetMyNotificationsHandler sut = CreateFilteringSut();

        var query = new GetMyNotificationsQuery(UserId: 1);
        Result<PagedCollectionResult<UserNotificationDTO>> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(i => i.Title).Should().BeEquivalentTo("Unscoped", "Event one", "Event two");
        result.Value.PaginationMetadata.TotalItemCount.Should().Be(3);
    }

    [Fact]
    public async Task HandleAsync_WithScope_ReturnsMatchingAndUnscopedOnly()
    {
        GetMyNotificationsHandler sut = CreateFilteringSut();

        var query = new GetMyNotificationsQuery(UserId: 1, ScopeKey: "event:2");
        Result<PagedCollectionResult<UserNotificationDTO>> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(i => i.Title).Should().BeEquivalentTo("Unscoped", "Event two");
        result.Value.PaginationMetadata.TotalItemCount.Should().Be(2);
    }

    [Fact]
    public async Task HandleAsync_WithWhitespaceScope_IsTreatedAsUnscoped()
    {
        GetMyNotificationsHandler sut = CreateFilteringSut();

        var query = new GetMyNotificationsQuery(UserId: 1, ScopeKey: "   ");
        Result<PagedCollectionResult<UserNotificationDTO>> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task HandleAsync_WithScopeMatchingNothing_ReturnsUnscopedOnly()
    {
        GetMyNotificationsHandler sut = CreateFilteringSut();

        var query = new GetMyNotificationsQuery(UserId: 1, ScopeKey: "event:99");
        Result<PagedCollectionResult<UserNotificationDTO>> result = await sut.HandleAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(i => i.Title).Should().BeEquivalentTo("Unscoped");
    }

    // ── Helpers ──

    /// <summary>
    /// Builds a handler over three real notifications (unscoped, "event:1", "event:2") in one user's
    /// inbox, with an executor that actually enumerates the composed query. Unlike
    /// <see cref="CreateSut"/>, which mocks the results outright, this pins the scope predicate itself.
    /// </summary>
    private static GetMyNotificationsHandler CreateFilteringSut()
    {
        List<PushNotification> pushNotifications =
        [
            Push(id: 1, title: "Unscoped", scopeKey: null),
            Push(id: 2, title: "Event one", scopeKey: "event:1"),
            Push(id: 3, title: "Event two", scopeKey: "event:2"),
        ];
        List<UserNotification> userNotifications =
            [.. pushNotifications.Select(pn => UserNotification.Create(userId: 1, pushNotificationId: pn.Id).Value!)];

        var unitOfWork = new Mock<IUnitOfWork>();
        var userNotificationRepo = new Mock<IRepository<UserNotification, UserNotificationIdentifierType>>();
        var pushNotificationRepo = new Mock<IRepository<PushNotification, PushNotificationIdentifierType>>();
        var queryableExecutor = new Mock<IQueryableExecutor>();

        unitOfWork.Setup(x => x.GetRepository<UserNotification, UserNotificationIdentifierType>())
            .Returns(userNotificationRepo.Object);
        unitOfWork.Setup(x => x.GetRepository<PushNotification, PushNotificationIdentifierType>())
            .Returns(pushNotificationRepo.Object);

        userNotificationRepo.Setup(x => x.TableNoTracking).Returns(userNotifications.AsQueryable());
        pushNotificationRepo.Setup(x => x.TableNoTracking).Returns(pushNotifications.AsQueryable());

        queryableExecutor.Setup(x => x.CountAsync(It.IsAny<IQueryable<UserNotificationDTO>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IQueryable<UserNotificationDTO> source, CancellationToken _) => source.Count());
        queryableExecutor.Setup(x => x.ToListAsync(It.IsAny<IQueryable<UserNotificationDTO>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IQueryable<UserNotificationDTO> source, CancellationToken _) => source.ToList());

        return new GetMyNotificationsHandler(unitOfWork.Object, queryableExecutor.Object);
    }

    /// <summary>
    /// Builds a notification that looks persisted. <c>Id</c> is <c>required init</c> and the factory
    /// leaves it at its default, so the identifier the join needs is written back through reflection
    /// rather than by opening the entity up with a test-only setter.
    /// </summary>
    private static PushNotification Push(PushNotificationIdentifierType id, string title, string? scopeKey)
    {
        PushNotification notification = PushNotification
            .Create(title, "Body", sentByUserId: 1, recipientCount: 1, scopeKey: scopeKey).Value!;
        typeof(PushNotification).GetProperty(nameof(PushNotification.Id))!.SetValue(notification, id);
        return notification;
    }

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
                Title = string.Create(CultureInfo.InvariantCulture, $"Title {i}"),
                Body = string.Create(CultureInfo.InvariantCulture, $"Body {i}"),
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
