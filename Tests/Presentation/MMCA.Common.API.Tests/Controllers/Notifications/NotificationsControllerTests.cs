using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Controllers.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.GetHistory;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Notifications;

public sealed class NotificationsControllerTests
{
    private readonly Mock<ICommandHandler<SendPushNotificationCommand, Result<PushNotificationDTO>>> _sendHandler = new();
    private readonly Mock<IQueryHandler<GetNotificationHistoryQuery, Result<PagedCollectionResult<PushNotificationDTO>>>> _historyHandler = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();

    private NotificationsController CreateController() =>
        new(_sendHandler.Object, _historyHandler.Object, _currentUserService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

    private static PushNotificationDTO CreateDto() =>
        new()
        {
            Id = 1,
            Title = "Test",
            Body = "Test body",
            SentByUserId = 42,
            RecipientCount = 10,
            Status = "Sent",
            CreatedOn = DateTime.UtcNow,
        };

    // ── SendAsync ──
    [Fact]
    public async Task SendAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);
        NotificationsController sut = CreateController();
        var request = new SendPushNotificationRequest("Title", "Body");

        ActionResult<PushNotificationDTO> result = await sut.SendAsync(request);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task SendAsync_Success_ReturnsCreated()
    {
        _currentUserService.Setup(s => s.UserId).Returns(42);
        PushNotificationDTO dto = CreateDto();
        _sendHandler
            .Setup(h => h.HandleAsync(It.IsAny<SendPushNotificationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));
        NotificationsController sut = CreateController();
        var request = new SendPushNotificationRequest("Title", "Body");

        ActionResult<PushNotificationDTO> result = await sut.SendAsync(request);

        var createdResult = result.Result as CreatedResult;
        createdResult.Should().NotBeNull();
        createdResult!.StatusCode.Should().Be(StatusCodes.Status201Created);
        createdResult.Value.Should().Be(dto);
    }

    [Fact]
    public async Task SendAsync_Failure_ReturnsHandleFailure()
    {
        _currentUserService.Setup(s => s.UserId).Returns(42);
        var error = Error.Validation("Send.Failed", "Validation failed");
        _sendHandler
            .Setup(h => h.HandleAsync(It.IsAny<SendPushNotificationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<PushNotificationDTO>(error));
        NotificationsController sut = CreateController();
        var request = new SendPushNotificationRequest("Title", "Body");

        ActionResult<PushNotificationDTO> result = await sut.SendAsync(request);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── GetHistoryAsync ──
    [Fact]
    public async Task GetHistoryAsync_Success_ReturnsOk()
    {
        PushNotificationDTO dto = CreateDto();
        var pagedResult = new PagedCollectionResult<PushNotificationDTO>(
            [dto],
            new PaginationMetadata(1, 10, 1));
        _historyHandler
            .Setup(h => h.HandleAsync(It.IsAny<GetNotificationHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(pagedResult));
        NotificationsController sut = CreateController();

        ActionResult<PagedCollectionResult<PushNotificationDTO>> result = await sut.GetHistoryAsync();

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value.Should().Be(pagedResult);
    }

    [Fact]
    public async Task GetHistoryAsync_Failure_ReturnsHandleFailure()
    {
        var error = Error.Failure("History.Failed", "Could not retrieve history");
        _historyHandler
            .Setup(h => h.HandleAsync(It.IsAny<GetNotificationHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<PagedCollectionResult<PushNotificationDTO>>(error));
        NotificationsController sut = CreateController();

        ActionResult<PagedCollectionResult<PushNotificationDTO>> result = await sut.GetHistoryAsync();

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
