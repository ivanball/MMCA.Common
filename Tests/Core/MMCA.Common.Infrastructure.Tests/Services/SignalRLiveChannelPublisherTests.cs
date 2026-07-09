using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using MMCA.Common.Infrastructure.Hubs;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class SignalRLiveChannelPublisherTests
{
    private readonly Mock<IHubContext<NotificationHub>> _mockHubContext = new();
    private readonly Mock<IHubClients> _mockClients = new();
    private readonly Mock<IClientProxy> _mockClientProxy = new();
    private readonly SignalRLiveChannelPublisher _sut;

    public SignalRLiveChannelPublisherTests()
    {
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);

        _sut = new SignalRLiveChannelPublisher(_mockHubContext.Object);
    }

    // ── PublishAsync ──
    [Fact]
    public async Task PublishAsync_SendsToChannelGroup()
    {
        await _sut.PublishAsync("event:1", "poll.results-changed", "{\"pollId\":7}");

        _mockClients.Verify(c => c.Group("event:1"), Times.Once);
        _mockClientProxy.Verify(
            p => p.SendCoreAsync(
                NotificationHub.ReceiveChannelEventMethod,
                It.Is<object?[]>(args =>
                    (string)args[0]! == "event:1" &&
                    (string)args[1]! == "poll.results-changed" &&
                    (string)args[2]! == "{\"pollId\":7}"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Cancellation token flows through ──
    [Fact]
    public async Task PublishAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();

        await _sut.PublishAsync("session:42", "question.approved", "{}", cts.Token);

        _mockClientProxy.Verify(
            p => p.SendCoreAsync(
                NotificationHub.ReceiveChannelEventMethod,
                It.IsAny<object?[]>(),
                cts.Token),
            Times.Once);
    }
}
