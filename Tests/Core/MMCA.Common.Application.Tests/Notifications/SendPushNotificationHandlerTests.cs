using System.Linq.Expressions;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;
using MMCA.Common.Application.UseCases.Markers;
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

    // -- HandleAsync: native (third) channel, ADR-044 --
    [Fact]
    public async Task HandleAsync_WithValidRequest_AlsoCallsNativePushSender()
    {
        var (sut, mocks) = CreateSut();

        SendPushNotificationCommand command = CreateCommand();
        await sut.HandleAsync(command);

        mocks.NativePushSender.Verify(
            x => x.SendToUsersAsync(
                It.IsAny<IEnumerable<UserIdentifierType>>(),
                "Test Title",
                "Test Body",
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenNativeSendFails_IsNonFatalAndStatusStaysSent()
    {
        var (sut, _) = CreateSut(nativeSendThrows: true);

        SendPushNotificationCommand command = CreateCommand();
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(nameof(PushNotificationStatus.Sent));
    }

    // -- HandleAsync: deduplication (gap 12) --
    [Fact]
    public async Task HandleAsync_WithDedupKeyAlreadySent_ReturnsExistingDTOWithoutAddOrSend()
    {
        PushNotification existing = CreateExisting("Existing Title", "Existing Body", "key-1");
        var (sut, mocks) = CreateSut(existingByDedupKey: existing);

        SendPushNotificationCommand command = CreateCommand() with { DedupKey = "key-1" };
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Title.Should().Be("Existing Title");
        result.Value.Body.Should().Be("Existing Body");

        mocks.NotificationRepo.Verify(
            x => x.AddAsync(It.IsAny<PushNotification>(), It.IsAny<CancellationToken>()),
            Times.Never);
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoSend(mocks);
    }

    [Fact]
    public async Task HandleAsync_WithUnseenDedupKey_SendsNormally()
    {
        var (sut, mocks) = CreateSut();

        SendPushNotificationCommand command = CreateCommand() with { DedupKey = "key-unseen" };
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(nameof(PushNotificationStatus.Sent));
        mocks.NotificationRepo.Verify(
            x => x.AddAsync(It.IsAny<PushNotification>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithNullDedupKey_TakesLegacyPathAndNeverQueriesForDuplicates()
    {
        var (sut, mocks) = CreateSut();

        SendPushNotificationCommand command = CreateCommand();
        command.DedupKey.Should().BeNull();
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(nameof(PushNotificationStatus.Sent));
        mocks.ReadRepo.Verify(AnyDedupLookup(), Times.Never);
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
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
    public async Task HandleAsync_WhenSaveHitsDedupUniqueIndex_RequeriesAndReturnsExistingDTO()
    {
        PushNotification winner = CreateExisting("Winner Title", "Winner Body", "key-2");
        var (sut, mocks) = CreateSut(saveThrows: true, dedupWinnerOnRequery: winner);

        SendPushNotificationCommand command = CreateCommand() with { DedupKey = "key-2" };
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Title.Should().Be("Winner Title");
        VerifyNoSend(mocks);
    }

    [Fact]
    public async Task HandleAsync_WhenSaveFailsAndDedupKeyIsStillAbsent_Rethrows()
    {
        var (sut, _) = CreateSut(saveThrows: true);

        SendPushNotificationCommand command = CreateCommand() with { DedupKey = "key-3" };

        Func<Task> act = () => sut.HandleAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // -- HandleAsync: scope key --
    [Fact]
    public async Task HandleAsync_WithScopedRequest_PersistsScopeKeyAndReturnsItOnTheDTO()
    {
        var (sut, mocks) = CreateSut();
        PushNotification? added = null;
        mocks.NotificationRepo
            .Setup(x => x.AddAsync(It.IsAny<PushNotification>(), It.IsAny<CancellationToken>()))
            .Callback<PushNotification, CancellationToken>((notification, _) => added = notification);

        SendPushNotificationCommand command = CreateCommand(scopeKey: "event:2");
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ScopeKey.Should().Be("event:2");
        added.Should().NotBeNull();
        added!.ScopeKey.Should().Be("event:2");
    }

    [Fact]
    public async Task HandleAsync_WithUnscopedRequest_LeavesScopeKeyNull()
    {
        var (sut, mocks) = CreateSut();
        PushNotification? added = null;
        mocks.NotificationRepo
            .Setup(x => x.AddAsync(It.IsAny<PushNotification>(), It.IsAny<CancellationToken>()))
            .Callback<PushNotification, CancellationToken>((notification, _) => added = notification);

        SendPushNotificationCommand command = CreateCommand();
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ScopeKey.Should().BeNull();
        added.Should().NotBeNull();
        added!.ScopeKey.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WithScopeKeyExceedingMaxLength_ReturnsFailure()
    {
        var (sut, _) = CreateSut();

        SendPushNotificationCommand command = CreateCommand(scopeKey: new string('x', PushNotification.ScopeKeyMaxLength + 1));
        Result<PushNotificationDTO> result = await sut.HandleAsync(command);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "PushNotification.ScopeKey.TooLong");
    }

    // -- Atomicity (M86) --

    // The handler commits the audit row (carrying DedupKey) before the recipient rows and the sends.
    // Without one transaction around the whole sequence, a fault in between leaves that row
    // committed and every retry of the same key short-circuits on it, reporting success for a
    // notification nobody ever received.
    [Fact]
    public void SendPushNotificationCommand_IsTransactional_SoAPartialSendRollsBack() =>
        typeof(SendPushNotificationCommand).Should().Implement<ITransactional>(
            "the audit row must roll back with the recipient rows, or a retry with the same DedupKey "
            + "short-circuits on an undelivered notification forever");

    [Fact]
    public async Task HandleAsync_WhenTheRecipientSaveThrows_PropagatesInsteadOfReportingSuccess()
    {
        var (sut, mocks) = CreateSut();

        // First save (the audit row) commits; the second (the per-recipient rows) faults.
        mocks.UnitOfWork.SetupSequence(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1)
            .ThrowsAsync(new InvalidOperationException("recipient rows failed"));

        Func<Task> act = () => sut.HandleAsync(CreateCommand());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("recipient rows failed");
        VerifyNoSend(mocks);
    }

    // -- Helpers --
    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IRepository<PushNotification, PushNotificationIdentifierType>> NotificationRepo,
        Mock<IReadRepository<PushNotification, PushNotificationIdentifierType>> ReadRepo,
        Mock<INotificationRecipientProvider> RecipientProvider,
        Mock<IPushNotificationSender> PushNotificationSender,
        Mock<INativePushSender> NativePushSender);

    private static PushNotification CreateExisting(string title, string body, string dedupKey) =>
        PushNotification.Create(title, body, sentByUserId: 7, recipientCount: 5, dedupKey: dedupKey).Value!;

    private static void VerifyNoSend(HandlerMocks mocks)
    {
        mocks.PushNotificationSender.Verify(
            x => x.SendToUsersAsync(
                It.IsAny<IEnumerable<UserIdentifierType>>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        mocks.NativePushSender.Verify(
            x => x.SendToUsersAsync(
                It.IsAny<IEnumerable<UserIdentifierType>>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Expression<Func<
        IReadRepository<PushNotification, PushNotificationIdentifierType>,
        Task<IReadOnlyCollection<PushNotification>>>> AnyDedupLookup() =>
        r => r.GetAllAsync(
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<Expression<Func<PushNotification, bool>>>(),
            It.IsAny<Expression<Func<PushNotification, string>>>(),
            It.IsAny<Expression<Func<PushNotification, PushNotification>>>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>());

    private static SendPushNotificationCommand CreateCommand(string? scopeKey = null) =>
        new(new SendPushNotificationRequest("Test Title", "Test Body") { ScopeKey = scopeKey }, SentByUserId: 1);

    private static (SendPushNotificationHandler Sut, HandlerMocks Mocks) CreateSut(
        int recipientCount = 3,
        bool sendThrows = false,
        bool nativeSendThrows = false,
        PushNotification? existingByDedupKey = null,
        bool saveThrows = false,
        PushNotification? dedupWinnerOnRequery = null)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var notificationRepo = new Mock<IRepository<PushNotification, PushNotificationIdentifierType>>();
        var userNotificationRepo = new Mock<IRepository<UserNotification, UserNotificationIdentifierType>>();
        var readRepo = new Mock<IReadRepository<PushNotification, PushNotificationIdentifierType>>();
        unitOfWork.Setup(x => x.GetRepository<PushNotification, PushNotificationIdentifierType>())
            .Returns(notificationRepo.Object);
        unitOfWork.Setup(x => x.GetRepository<UserNotification, UserNotificationIdentifierType>())
            .Returns(userNotificationRepo.Object);
        unitOfWork.Setup(x => x.GetReadRepository<PushNotification, PushNotificationIdentifierType>())
            .Returns(readRepo.Object);

        if (saveThrows)
        {
            // Only the first save fails (the DedupKey unique-index violation); later saves succeed.
            unitOfWork.SetupSequence(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Violation of UNIQUE KEY constraint on DedupKey"))
                .ReturnsAsync(1);
        }
        else
        {
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);
        }

        if (dedupWinnerOnRequery is null)
        {
            IReadOnlyCollection<PushNotification> dedupMatches =
                existingByDedupKey is null ? [] : [existingByDedupKey];
            readRepo.Setup(AnyDedupLookup()).ReturnsAsync(dedupMatches);
        }
        else
        {
            // First lookup (the pre-check) sees nothing; the post-violation requery finds the winner.
            IReadOnlyCollection<PushNotification> noMatch = [];
            IReadOnlyCollection<PushNotification> winnerMatch = [dedupWinnerOnRequery];
            readRepo.SetupSequence(AnyDedupLookup())
                .ReturnsAsync(noMatch)
                .ReturnsAsync(winnerMatch);
        }

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

        var nativePushSender = new Mock<INativePushSender>();
        if (nativeSendThrows)
        {
            nativePushSender.Setup(x => x.SendToUsersAsync(
                    It.IsAny<IEnumerable<UserIdentifierType>>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Notification hub unreachable"));
        }

        var dtoMapper = new PushNotificationDTOMapper();

        var sut = new SendPushNotificationHandler(
            unitOfWork.Object,
            recipientProvider.Object,
            pushNotificationSender.Object,
            nativePushSender.Object,
            dtoMapper,
            NullLogger<SendPushNotificationHandler>.Instance);

        var mocks = new HandlerMocks(
            unitOfWork,
            notificationRepo,
            readRepo,
            recipientProvider,
            pushNotificationSender,
            nativePushSender);

        return (sut, mocks);
    }
}
