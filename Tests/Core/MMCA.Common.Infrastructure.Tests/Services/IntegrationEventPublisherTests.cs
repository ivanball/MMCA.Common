using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class IntegrationEventPublisherTests
{
    // ── Publishes event through event bus ──
    [Fact]
    public async Task PublishAsync_DelegatesToEventBus()
    {
        var eventBus = new Mock<IEventBus>();
        var sut = new IntegrationEventPublisher(eventBus.Object);
        var integrationEvent = new Mock<IIntegrationEvent>();

        await sut.PublishAsync(integrationEvent.Object, CancellationToken.None);

        eventBus.Verify(
            x => x.PublishAsync(integrationEvent.Object, CancellationToken.None),
            Times.Once);
    }

    // ── Passes cancellation token ──
    [Fact]
    public async Task PublishAsync_PassesCancellationToken()
    {
        var eventBus = new Mock<IEventBus>();
        var sut = new IntegrationEventPublisher(eventBus.Object);
        var integrationEvent = new Mock<IIntegrationEvent>();
        using var cts = new CancellationTokenSource();

        await sut.PublishAsync(integrationEvent.Object, cts.Token);

        eventBus.Verify(
            x => x.PublishAsync(integrationEvent.Object, cts.Token),
            Times.Once);
    }
}
