using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using Moq;

namespace MMCA.Common.Application.Tests.Notifications;

public class SendPushNotificationHandlerTests
{
    // -- HandleAsync: valid request --
    [Fact]
    public async Task HandleAsync_WithValidRequest_ReturnsSuccessWithDTO()
    {
        var (sut, _) = CreateSut();

        SendPushNotificationCommand command = CreateCommand();
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Title.Should().Be("Test Title");
        result.Value.Body.Should().Be("Test Body");
        result.Value.SentByUserId.Should().Be(1);
        result.Value.RecipientCount.Should().Be(3);
    }

    [Fact]
    public async Task HandleAsync_WithValidRequest_CallsPushNotificationSender()
    {
        var (sut, mocks) = CreateSut();

        SendPushNotificationCommand command = CreateCommand();
        await sut.HandleAsync(command);

        mocks.PushNotificationSender.Verify(
            x => x.SendToUsersAsync(
                It.IsAny<IEnumerable<UserIdentifierType>>(),
                "Test Title",
                "Test Body",
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithValidRequest_SavesThreeTimes()
    {
        var (sut, mocks) = CreateSut();

        SendPushNotificationCommand command = CreateCommand();
        await sut.HandleAsync(command);

        // First save: persist entity, second save: user notifications, third save: update status
        mocks.UnitOfWork.Verify(
            x => x.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task HandleAsync_WhenSendSucceeds_StatusIsSent()
    {
        var (sut, _) = CreateSut();

        SendPushNotificationCommand command = CreateCommand();
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.Value!.Status.Should().Be(nameof(PushNotificationStatus.Sent));
    }

    // -- HandleAsync: no recipients --
    [Fact]
    public async Task HandleAsync_WhenNoRecipients_ReturnsValidationFailure()
    {
        var (sut, _) = CreateSut(recipientCount: 0);

        SendPushNotificationCommand command = CreateCommand();
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "PushNotification.NoRecipients");
    }

    // -- HandleAsync: send failure --
    [Fact]
    public async Task HandleAsync_WhenSendFails_StatusIsFailed()
    {
        var (sut, _) = CreateSut(sendThrows: true);

        SendPushNotificationCommand command = CreateCommand();
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(nameof(PushNotificationStatus.Failed));
    }

    // -- HandleAsync: entity creation failure --
    [Fact]
    public async Task HandleAsync_WhenCreateFails_ReturnsFailure()
    {
        var (sut, _) = CreateSut();

        // Empty title violates PushNotification invariants
        var command = new SendPushNotificationCommand(
            new SendPushNotificationRequest(string.Empty, "Test Body"),
            SentByUserId: 1);

        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "PushNotification.Title.Empty");
    }

    // -- Helpers --
    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IRepository<PushNotification, PushNotificationIdentifierType>> NotificationRepo,
        Mock<INotificationRecipientProvider> RecipientProvider,
        Mock<IPushNotificationSender> PushNotificationSender);

    private static SendPushNotificationCommand CreateCommand() =>
        new(new SendPushNotificationRequest("Test Title", "Test Body"), SentByUserId: 1);

    private static (SendPushNotificationHandler Sut, HandlerMocks Mocks) CreateSut(
        int recipientCount = 3,
        bool sendThrows = false)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var notificationRepo = new Mock<IRepository<PushNotification, PushNotificationIdentifierType>>();
        var userNotificationRepo = new Mock<IRepository<UserNotification, UserNotificationIdentifierType>>();
        unitOfWork.Setup(x => x.GetRepository<PushNotification, PushNotificationIdentifierType>())
            .Returns(notificationRepo.Object);
        unitOfWork.Setup(x => x.GetRepository<UserNotification, UserNotificationIdentifierType>())
            .Returns(userNotificationRepo.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var recipientProvider = new Mock<INotificationRecipientProvider>();
        IReadOnlyList<UserIdentifierType> recipientIds = [.. Enumerable.Range(100, recipientCount)];
        recipientProvider.Setup(x => x.GetRecipientUserIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(recipientIds);

        var pushNotificationSender = new Mock<IPushNotificationSender>();
        if (sendThrows)
        {
            pushNotificationSender.Setup(x => x.SendToUsersAsync(
                    It.IsAny<IEnumerable<UserIdentifierType>>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("SignalR connection failed"));
        }

        var dtoMapper = new PushNotificationDTOMapper();

        var sut = new SendPushNotificationHandler(
            unitOfWork.Object,
            recipientProvider.Object,
            pushNotificationSender.Object,
            dtoMapper,
            NullLogger<SendPushNotificationHandler>.Instance);

        var mocks = new HandlerMocks(
            unitOfWork,
            notificationRepo,
            recipientProvider,
            pushNotificationSender);

        return (sut, mocks);
    }
}
