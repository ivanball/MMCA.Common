using AwesomeAssertions;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Azure.Notifications;
using Moq;

namespace MMCA.Common.Azure.Tests.Notifications;

/// <summary>
/// The Azure Notification Hubs native push sender (ADR-044): every 20-tag chunk is sent on both
/// platforms, a failing send does not stop the later chunks or the other platform, and the outcome
/// counts batches per platform and carries the first failure instead of throwing it.
/// </summary>
public sealed class AzureNotificationHubNativePushSenderTests
{
    private readonly Mock<INotificationHubClient> _hubClient = new();
    private readonly List<(string Platform, string TagExpression)> _sends = [];

    private AzureNotificationHubNativePushSender CreateSut(Func<Notification, string, Exception?> failure)
    {
        _hubClient
            .Setup(x => x.SendNotificationAsync(It.IsAny<Notification>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Notification, string, CancellationToken>((notification, tagExpression, _) =>
            {
                _sends.Add((notification is AppleNotification ? "apns" : "fcmv1", tagExpression));
                var exception = failure(notification, tagExpression);
                return exception is null
                    ? Task.FromResult(new NotificationOutcome())
                    : Task.FromException<NotificationOutcome>(exception);
            });

        return new AzureNotificationHubNativePushSender(_hubClient.Object, NullLogger<AzureNotificationHubNativePushSender>.Instance);
    }

    // 25 users span two OR-chunks at the hub's 20-tag cap.
    private static IEnumerable<UserIdentifierType> TwoChunksOfUsers() => Enumerable.Range(1, 25);

    // 45 users span three OR-chunks.
    private static IEnumerable<UserIdentifierType> ThreeChunksOfUsers() => Enumerable.Range(1, 45);

    [Fact]
    public async Task SendToUsersAsync_SendsEveryChunkOnBothPlatforms()
    {
        var sut = CreateSut((_, _) => null);

        var outcome = await sut.SendToUsersAsync(TwoChunksOfUsers(), "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _sends.Select(s => s.Platform).Should().Equal("fcmv1", "apns", "fcmv1", "apns");
        _sends.Select(s => s.TagExpression).Distinct().Should().HaveCount(2);
        outcome.Android.Should().Be(new NativePushPlatformCounts(Sent: 2, Failed: 0));
        outcome.Apple.Should().Be(new NativePushPlatformCounts(Sent: 2, Failed: 0));
        outcome.Unit.Should().Be(NativePushDeliveryUnit.Batch);
        outcome.HasFailures.Should().BeFalse();
        outcome.FirstFailure.Should().BeNull();
    }

    // Spec B assertion 1: the hub reports per batch only, so the outcome counts batches per platform,
    // and a failing batch is counted and returned instead of thrown.
    [Fact]
    public async Task SendToUsersAsync_OneFailingAppleBatchOutOfThree_ReportsAndroidThreeOfThree_AppleTwoSentOneFailed_AndDoesNotThrow()
    {
        string? firstChunk = null;
        var sut = CreateSut((notification, tagExpression) =>
        {
            firstChunk ??= tagExpression;
            return notification is AppleNotification && tagExpression == firstChunk
                ? new InvalidOperationException("APNs rejected the send")
                : null;
        });

        var outcome = await sut.SendToUsersAsync(ThreeChunksOfUsers(), "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        outcome.Android.Should().Be(new NativePushPlatformCounts(Sent: 3, Failed: 0));
        outcome.Apple.Should().Be(new NativePushPlatformCounts(Sent: 2, Failed: 1));
        outcome.FirstFailure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("APNs rejected the send");
        outcome.HasFailures.Should().BeTrue();
        _sends.Should().HaveCount(6, "every chunk still sends on both platforms after the failure");
    }

    [Fact]
    public async Task SendToUsersAsync_SeveralFailures_AreCountedPerPlatform_AndTheFirstIsReturned()
    {
        var sut = CreateSut((notification, _) => notification is AppleNotification ? new InvalidOperationException("APNs down") : null);

        var outcome = await sut.SendToUsersAsync(TwoChunksOfUsers(), "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        outcome.Android.Should().Be(new NativePushPlatformCounts(Sent: 2, Failed: 0));
        outcome.Apple.Should().Be(new NativePushPlatformCounts(Sent: 0, Failed: 2));
        outcome.FirstFailure.Should().BeOfType<InvalidOperationException>();
        _sends.Should().HaveCount(4);
    }

    [Fact]
    public async Task SendToUsersAsync_NoUsers_AttemptsNothing()
    {
        var sut = CreateSut((_, _) => null);

        var outcome = await sut.SendToUsersAsync([], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        outcome.Android.Should().BeNull();
        outcome.Apple.Should().BeNull();
        outcome.HasFailures.Should().BeFalse();
        _sends.Should().BeEmpty();
    }

    [Fact]
    public async Task SendToUsersAsync_TheCallersCancellation_StopsTheLoop()
    {
        using var cts = new CancellationTokenSource();
        var sut = CreateSut((_, _) =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        });

        var act = () => sut.SendToUsersAsync(TwoChunksOfUsers(), "Hello", "World", cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _sends.Should().ContainSingle();
    }
}
