using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using MMCA.Common.Infrastructure.Hubs;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

/// <summary>
/// Additional tests for <see cref="SignalRPushNotificationSender"/> covering metadata,
/// cancellation tokens, and edge cases not in the base test file.
/// </summary>
public sealed class SignalRPushNotificationSenderAdditionalTests
{
    private readonly Mock<IHubContext<NotificationHub>> _mockHubContext = new();
    private readonly Mock<IHubClients> _mockClients = new();
    private readonly Mock<IClientProxy> _mockClientProxy = new();
    private readonly SignalRPushNotificationSender _sut;

    public SignalRPushNotificationSenderAdditionalTests()
    {
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
        _mockClients.Setup(c => c.User(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockClients.Setup(c => c.Users(It.IsAny<IReadOnlyList<string>>())).Returns(_mockClientProxy.Object);
        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);

        _sut = new SignalRPushNotificationSender(_mockHubContext.Object);
    }

    // ── SendToUserAsync with metadata ──
    [Fact]
    public async Task SendToUserAsync_WithMetadata_PassesMetadataToHub()
    {
        var metadata = new Dictionary<string, string>
        {
            ["key1"] = "value1",
            ["key2"] = "value2"
        };

        await _sut.SendToUserAsync(42, "Title", "Body", metadata);

        _mockClientProxy.Verify(
            p => p.SendCoreAsync(
                NotificationHub.ReceiveNotificationMethod,
                It.Is<object?[]>(args => args.Length >= 3 && args[2] == metadata),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── SendToUserAsync with null metadata ──
    [Fact]
    public async Task SendToUserAsync_WithNullMetadata_PassesNullToHub()
    {
        await _sut.SendToUserAsync(1, "Title", "Body", null);

        _mockClientProxy.Verify(
            p => p.SendCoreAsync(
                NotificationHub.ReceiveNotificationMethod,
                It.Is<object?[]>(args => args.Length >= 3 && args[2] == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── SendToUsersAsync with empty user list ──
    [Fact]
    public async Task SendToUsersAsync_EmptyList_DoesNotSend()
    {
        int[] emptyIds = [];

        await _sut.SendToUsersAsync(emptyIds, "Title", "Body");

        _mockClients.Verify(
            c => c.Users(It.IsAny<IReadOnlyList<string>>()),
            Times.Never);
    }

    // ── SendToUsersAsync with exactly batch-size users (boundary) ──
    [Fact]
    public async Task SendToUsersAsync_ExactBatchSize_SingleBatch()
    {
        int[] userIds = [.. Enumerable.Range(1, 100)];

        await _sut.SendToUsersAsync(userIds, "Title", "Body");

        // Exactly 100 users = 1 batch of 100
        _mockClients.Verify(
            c => c.Users(It.IsAny<IReadOnlyList<string>>()),
            Times.Once);
    }

    // ── SendToUsersAsync with batch-size + 1 (triggers second batch) ──
    [Fact]
    public async Task SendToUsersAsync_BatchSizePlusOne_TwoBatches()
    {
        int[] userIds = [.. Enumerable.Range(1, 101)];

        await _sut.SendToUsersAsync(userIds, "Title", "Body");

        // 101 users = 2 batches (100 + 1)
        _mockClients.Verify(
            c => c.Users(It.IsAny<IReadOnlyList<string>>()),
            Times.Exactly(2));
    }

    // ── SendToUsersAsync with metadata ──
    [Fact]
    public async Task SendToUsersAsync_WithMetadata_PassesMetadataToHub()
    {
        var metadata = new Dictionary<string, string> { ["action"] = "navigate" };

        await _sut.SendToUsersAsync([1, 2], "Title", "Body", metadata);

        _mockClientProxy.Verify(
            p => p.SendCoreAsync(
                NotificationHub.ReceiveNotificationMethod,
                It.Is<object?[]>(args => args.Length >= 3 && args[2] == metadata),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── BroadcastAsync with metadata ──
    [Fact]
    public async Task BroadcastAsync_WithMetadata_PassesMetadataToHub()
    {
        var metadata = new Dictionary<string, string> { ["channel"] = "general" };

        await _sut.BroadcastAsync("Title", "Body", metadata);

        _mockClientProxy.Verify(
            p => p.SendCoreAsync(
                NotificationHub.ReceiveNotificationMethod,
                It.Is<object?[]>(args => args.Length >= 3 && args[2] == metadata),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── SendToUserAsync with cancellation token ──
    [Fact]
    public async Task SendToUserAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();

        await _sut.SendToUserAsync(1, "Title", "Body", cancellationToken: cts.Token);

        _mockClientProxy.Verify(
            p => p.SendCoreAsync(
                NotificationHub.ReceiveNotificationMethod,
                It.IsAny<object?[]>(),
                cts.Token),
            Times.Once);
    }

    // ── BroadcastAsync with cancellation token ──
    [Fact]
    public async Task BroadcastAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();

        await _sut.BroadcastAsync("Title", "Body", cancellationToken: cts.Token);

        _mockClientProxy.Verify(
            p => p.SendCoreAsync(
                NotificationHub.ReceiveNotificationMethod,
                It.IsAny<object?[]>(),
                cts.Token),
            Times.Once);
    }
}
