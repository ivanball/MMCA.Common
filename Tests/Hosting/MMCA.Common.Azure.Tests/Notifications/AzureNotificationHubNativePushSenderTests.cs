using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Azure.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Azure.Tests.Notifications;

/// <summary>
/// The Azure Notification Hubs native push sender (ADR-044): every 20-tag chunk is sent on both
/// platforms, a failing send does not stop the later chunks or the other platform, and the outcome
/// counts batches per platform and carries the first failure instead of throwing it.
/// </summary>
public sealed class AzureNotificationHubNativePushSenderTests
{
    private readonly FakeNotificationHubRestClient _hubClient = new();

    private IEnumerable<(string Format, string TagExpression)> Sends =>
        _hubClient.Sends.Select(s => (s.Notification.Format, s.TagExpression));

    private static bool IsApple(NotificationHubNotification notification) => notification.Format == "apple";

    private AzureNotificationHubNativePushSender CreateSut(Func<NotificationHubNotification, string, Exception?> failure)
    {
        _hubClient.SendFailure = failure;
        return new AzureNotificationHubNativePushSender(_hubClient, NullLogger<AzureNotificationHubNativePushSender>.Instance);
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

        Sends.Select(s => s.Format).Should().Equal("fcmv1", "apple", "fcmv1", "apple");
        Sends.Select(s => s.TagExpression).Distinct().Should().HaveCount(2);
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
            return IsApple(notification) && tagExpression == firstChunk
                ? new InvalidOperationException("APNs rejected the send")
                : null;
        });

        var outcome = await sut.SendToUsersAsync(ThreeChunksOfUsers(), "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        outcome.Android.Should().Be(new NativePushPlatformCounts(Sent: 3, Failed: 0));
        outcome.Apple.Should().Be(new NativePushPlatformCounts(Sent: 2, Failed: 1));
        outcome.FirstFailure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("APNs rejected the send");
        outcome.HasFailures.Should().BeTrue();
        Sends.Should().HaveCount(6, "every chunk still sends on both platforms after the failure");
    }

    [Fact]
    public async Task SendToUsersAsync_SeveralFailures_AreCountedPerPlatform_AndTheFirstIsReturned()
    {
        var sut = CreateSut((notification, _) => IsApple(notification) ? new InvalidOperationException("APNs down") : null);

        var outcome = await sut.SendToUsersAsync(TwoChunksOfUsers(), "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        outcome.Android.Should().Be(new NativePushPlatformCounts(Sent: 2, Failed: 0));
        outcome.Apple.Should().Be(new NativePushPlatformCounts(Sent: 0, Failed: 2));
        outcome.FirstFailure.Should().BeOfType<InvalidOperationException>();
        Sends.Should().HaveCount(4);
    }

    [Fact]
    public async Task SendToUsersAsync_NoUsers_AttemptsNothing()
    {
        var sut = CreateSut((_, _) => null);

        var outcome = await sut.SendToUsersAsync([], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        outcome.Android.Should().BeNull();
        outcome.Apple.Should().BeNull();
        outcome.HasFailures.Should().BeFalse();
        Sends.Should().BeEmpty();
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
        Sends.Should().ContainSingle();
    }

    [Fact]
    public async Task SendToUsersAsync_SendsTheSharedPlatformPayloads_ToTheUserTagExpression()
    {
        var sut = CreateSut((_, _) => null);
        var metadata = new Dictionary<string, string> { ["route"] = "/events/1" };

        await sut.SendToUsersAsync([7, 8], "Hello", "World", metadata, TestContext.Current.CancellationToken);

        _hubClient.Sends.Should().HaveCount(2);
        _hubClient.Sends[0].TagExpression.Should().Be("user:7 || user:8");
        _hubClient.Sends[0].Notification.Should().Be(NotificationHubNotification.FcmV1(NativePushPayloads.BuildFcmV1Payload("Hello", "World", metadata)));
        _hubClient.Sends[1].Notification.Should().Be(NotificationHubNotification.Apple(NativePushPayloads.BuildApnsPayload("Hello", "World", metadata)));
    }

    [Fact]
    public async Task SendToUsersAsync_AHubFailure_IsCountedNotThrown()
    {
        var sut = CreateSut((notification, _) => IsApple(notification) ? FakeNotificationHubRestClient.ServerBusy() : null);

        var outcome = await sut.SendToUsersAsync([1], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        outcome.Apple.Should().Be(new NativePushPlatformCounts(Sent: 0, Failed: 1));
        outcome.FirstFailure.Should().BeOfType<NotificationHubException>();
    }
}
