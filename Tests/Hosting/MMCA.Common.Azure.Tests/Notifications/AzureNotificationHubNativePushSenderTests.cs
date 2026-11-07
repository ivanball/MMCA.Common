using AwesomeAssertions;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Azure.Notifications;
using Moq;

namespace MMCA.Common.Azure.Tests.Notifications;

/// <summary>
/// The Azure Notification Hubs native push sender (ADR-044): every 20-tag chunk is sent on both
/// platforms, a failing send does not stop the later chunks or the other platform, and the
/// failures are rethrown once the loop ends (one as itself, several as an aggregate) so the
/// caller's best-effort catch still sees them.
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

    [Fact]
    public async Task SendToUsersAsync_SendsEveryChunkOnBothPlatforms()
    {
        var sut = CreateSut((_, _) => null);

        await sut.SendToUsersAsync(TwoChunksOfUsers(), "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _sends.Select(s => s.Platform).Should().Equal("fcmv1", "apns", "fcmv1", "apns");
        _sends.Select(s => s.TagExpression).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task SendToUsersAsync_AnApnsFailure_DoesNotStopTheLaterChunks_AndIsRethrownAsItself()
    {
        string? firstChunk = null;
        var sut = CreateSut((notification, tagExpression) =>
        {
            firstChunk ??= tagExpression;
            return notification is AppleNotification && tagExpression == firstChunk
                ? new InvalidOperationException("APNs rejected the send")
                : null;
        });

        var act = () => sut.SendToUsersAsync(TwoChunksOfUsers(), "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("APNs rejected the send");
        _sends.Select(s => s.Platform).Should().Equal(["fcmv1", "apns", "fcmv1", "apns"], "the second chunk still sends on both platforms");
    }

    [Fact]
    public async Task SendToUsersAsync_SeveralFailures_AreRethrownTogetherAsAnAggregate()
    {
        var sut = CreateSut((notification, _) => notification is AppleNotification ? new InvalidOperationException("APNs down") : null);

        var act = () => sut.SendToUsersAsync(TwoChunksOfUsers(), "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowExactlyAsync<AggregateException>();
        thrown.Which.InnerExceptions.Should().HaveCount(2).And.AllBeOfType<InvalidOperationException>();
        _sends.Should().HaveCount(4);
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
