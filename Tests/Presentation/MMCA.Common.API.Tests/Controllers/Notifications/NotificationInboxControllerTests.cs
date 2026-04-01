using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Controllers.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetInbox;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetUnreadCount;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkRead;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.UserNotifications;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Notifications;

public sealed class NotificationInboxControllerTests
{
    private readonly Mock<IQueryHandler<GetMyNotificationsQuery, Result<PagedCollectionResult<UserNotificationDTO>>>> _inboxHandlerMock = new();
    private readonly Mock<IQueryHandler<GetUnreadNotificationCountQuery, Result<int>>> _unreadCountHandlerMock = new();
    private readonly Mock<ICommandHandler<MarkNotificationReadCommand, Result>> _markReadHandlerMock = new();
    private readonly Mock<ICommandHandler<MarkAllNotificationsReadCommand, Result>> _markAllReadHandlerMock = new();
    private readonly Mock<ICurrentUserService> _currentUserServiceMock = new();

    private InboxController CreateController() =>
        new(
            _inboxHandlerMock.Object,
            _unreadCountHandlerMock.Object,
            _markReadHandlerMock.Object,
            _markAllReadHandlerMock.Object,
            _currentUserServiceMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

    // ── GetInboxAsync ─────────────────────────────────────────────────────────
    [Fact]
    public async Task GetInboxAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns((int?)null);
        InboxController sut = CreateController();

        ActionResult<PagedCollectionResult<UserNotificationDTO>> result = await sut.GetInboxAsync();

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task GetInboxAsync_Success_ReturnsOkWithPagedResult()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(42);
        var pagedResult = new PagedCollectionResult<UserNotificationDTO>(
            items: [CreateSampleNotification()],
            paginationMetadata: new PaginationMetadata(1, 20, 1));
        _inboxHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<GetMyNotificationsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(pagedResult));
        InboxController sut = CreateController();

        ActionResult<PagedCollectionResult<UserNotificationDTO>> result = await sut.GetInboxAsync();

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value.Should().BeSameAs(pagedResult);
    }

    [Fact]
    public async Task GetInboxAsync_Success_PassesCorrectQueryParameters()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(42);
        GetMyNotificationsQuery? capturedQuery = null;
        _inboxHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<GetMyNotificationsQuery>(), It.IsAny<CancellationToken>()))
            .Callback<GetMyNotificationsQuery, CancellationToken>((q, _) => capturedQuery = q)
            .ReturnsAsync(Result.Success(new PagedCollectionResult<UserNotificationDTO>()));
        InboxController sut = CreateController();

        await sut.GetInboxAsync(pageNumber: 3, pageSize: 10);

        capturedQuery.Should().NotBeNull();
        capturedQuery!.UserId.Should().Be(42);
        capturedQuery.PageNumber.Should().Be(3);
        capturedQuery.PageSize.Should().Be(10);
    }

    [Fact]
    public async Task GetInboxAsync_Failure_ReturnsHandleFailure()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(42);
        _inboxHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<GetMyNotificationsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<PagedCollectionResult<UserNotificationDTO>>(
                Error.NotFoundError("Notification.NotFound", "Not found")));
        InboxController sut = CreateController();

        ActionResult<PagedCollectionResult<UserNotificationDTO>> result = await sut.GetInboxAsync();

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // ── GetUnreadCountAsync ───────────────────────────────────────────────────
    [Fact]
    public async Task GetUnreadCountAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns((int?)null);
        InboxController sut = CreateController();

        ActionResult<int> result = await sut.GetUnreadCountAsync();

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task GetUnreadCountAsync_Success_ReturnsOkWithCount()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(42);
        _unreadCountHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<GetUnreadNotificationCountQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(5));
        InboxController sut = CreateController();

        ActionResult<int> result = await sut.GetUnreadCountAsync();

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value.Should().Be(5);
    }

    [Fact]
    public async Task GetUnreadCountAsync_Success_PassesCorrectUserId()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(99);
        GetUnreadNotificationCountQuery? capturedQuery = null;
        _unreadCountHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<GetUnreadNotificationCountQuery>(), It.IsAny<CancellationToken>()))
            .Callback<GetUnreadNotificationCountQuery, CancellationToken>((q, _) => capturedQuery = q)
            .ReturnsAsync(Result.Success(0));
        InboxController sut = CreateController();

        await sut.GetUnreadCountAsync();

        capturedQuery.Should().NotBeNull();
        capturedQuery!.UserId.Should().Be(99);
    }

    [Fact]
    public async Task GetUnreadCountAsync_Failure_ReturnsHandleFailure()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(42);
        _unreadCountHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<GetUnreadNotificationCountQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<int>(
                Error.Failure("Notification.Error", "Something went wrong")));
        InboxController sut = CreateController();

        ActionResult<int> result = await sut.GetUnreadCountAsync();

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── MarkReadAsync ─────────────────────────────────────────────────────────
    [Fact]
    public async Task MarkReadAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns((int?)null);
        InboxController sut = CreateController();

        IActionResult result = await sut.MarkReadAsync(1);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task MarkReadAsync_Success_ReturnsNoContent()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(42);
        _markReadHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<MarkNotificationReadCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        InboxController sut = CreateController();

        IActionResult result = await sut.MarkReadAsync(7);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task MarkReadAsync_Success_PassesCorrectCommand()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(42);
        MarkNotificationReadCommand? capturedCommand = null;
        _markReadHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<MarkNotificationReadCommand>(), It.IsAny<CancellationToken>()))
            .Callback<MarkNotificationReadCommand, CancellationToken>((c, _) => capturedCommand = c)
            .ReturnsAsync(Result.Success());
        InboxController sut = CreateController();

        await sut.MarkReadAsync(7);

        capturedCommand.Should().NotBeNull();
        capturedCommand!.NotificationId.Should().Be(7);
        capturedCommand.UserId.Should().Be(42);
    }

    [Fact]
    public async Task MarkReadAsync_Failure_ReturnsHandleFailure()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(42);
        _markReadHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<MarkNotificationReadCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                Error.NotFoundError("Notification.NotFound", "Notification not found")));
        InboxController sut = CreateController();

        IActionResult result = await sut.MarkReadAsync(999);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // ── MarkAllReadAsync ──────────────────────────────────────────────────────
    [Fact]
    public async Task MarkAllReadAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns((int?)null);
        InboxController sut = CreateController();

        IActionResult result = await sut.MarkAllReadAsync();

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task MarkAllReadAsync_Success_ReturnsNoContent()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(42);
        _markAllReadHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<MarkAllNotificationsReadCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        InboxController sut = CreateController();

        IActionResult result = await sut.MarkAllReadAsync();

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task MarkAllReadAsync_Success_PassesCorrectUserId()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(77);
        MarkAllNotificationsReadCommand? capturedCommand = null;
        _markAllReadHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<MarkAllNotificationsReadCommand>(), It.IsAny<CancellationToken>()))
            .Callback<MarkAllNotificationsReadCommand, CancellationToken>((c, _) => capturedCommand = c)
            .ReturnsAsync(Result.Success());
        InboxController sut = CreateController();

        await sut.MarkAllReadAsync();

        capturedCommand.Should().NotBeNull();
        capturedCommand!.UserId.Should().Be(77);
    }

    [Fact]
    public async Task MarkAllReadAsync_Failure_ReturnsHandleFailure()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(42);
        _markAllReadHandlerMock
            .Setup(h => h.HandleAsync(It.IsAny<MarkAllNotificationsReadCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                Error.Failure("Notification.Error", "Could not mark all as read")));
        InboxController sut = CreateController();

        IActionResult result = await sut.MarkAllReadAsync();

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static UserNotificationDTO CreateSampleNotification() =>
        new()
        {
            Id = 1,
            PushNotificationId = 10,
            Title = "Test Notification",
            Body = "Test body content",
            IsRead = false,
            SentOn = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)
        };
}
