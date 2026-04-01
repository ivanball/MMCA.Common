using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using MMCA.Common.Infrastructure.Hubs;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class SignalRPushNotificationSenderTests
{
    private readonly Mock<IHubContext<NotificationHub>> _mockHubContext = new();
    private readonly Mock<IHubClients> _mockClients = new();
    private readonly Mock<IClientProxy> _mockClientProxy = new();
    private readonly SignalRPushNotificationSender _sut;

    public SignalRPushNotificationSenderTests()
    {
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
        _mockClients.Setup(c => c.User(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockClients.Setup(c => c.Users(It.IsAny<IReadOnlyList<string>>())).Returns(_mockClientProxy.Object);
        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);

        _sut = new SignalRPushNotificationSender(_mockHubContext.Object);
    }

    // ── SendToUserAsync ──
    [Fact]
    public async Task SendToUserAsync_SendsViaHubContext()
    {
        const int userId = 42;

        await _sut.SendToUserAsync(userId, "Test Title", "Test Body");

        _mockClients.Verify(c => c.User("42"), Times.Once);
        _mockClientProxy.Verify(
            p => p.SendCoreAsync(
                NotificationHub.ReceiveNotificationMethod,
                It.Is<object?[]>(args =>
                    (string)args[0]! == "Test Title" &&
                    (string)args[1]! == "Test Body"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── SendToUsersAsync — small list (single batch) ──
    [Fact]
    public async Task SendToUsersAsync_SmallList_SingleBatch()
    {
        int[] userIds = [.. Enumerable.Range(1, 50)];

        await _sut.SendToUsersAsync(userIds, "Title", "Body");

        _mockClients.Verify(
            c => c.Users(It.IsAny<IReadOnlyList<string>>()),
            Times.Once);
    }

    // ── SendToUsersAsync — large list (multiple batches) ──
    [Fact]
    public async Task SendToUsersAsync_LargeList_MultipleBatches()
    {
        int[] userIds = [.. Enumerable.Range(1, 250)];

        await _sut.SendToUsersAsync(userIds, "Title", "Body");

        // 250 users / 100 per batch = 3 batches (100 + 100 + 50)
        _mockClients.Verify(
            c => c.Users(It.IsAny<IReadOnlyList<string>>()),
            Times.Exactly(3));
    }

    // ── BroadcastAsync ──
    [Fact]
    public async Task BroadcastAsync_SendsToAllClients()
    {
        await _sut.BroadcastAsync("Broadcast Title", "Broadcast Body");

        _mockClients.Verify(c => c.All, Times.Once);
        _mockClientProxy.Verify(
            p => p.SendCoreAsync(
                NotificationHub.ReceiveNotificationMethod,
                It.Is<object?[]>(args =>
                    (string)args[0]! == "Broadcast Title" &&
                    (string)args[1]! == "Broadcast Body"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
