using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class IntegrationEventConsumerTests
{
    public sealed record class TestIntegrationEvent : BaseIntegrationEvent;

    private static Mock<ConsumeContext<TestIntegrationEvent>> ContextFor(TestIntegrationEvent evt)
    {
        var context = new Mock<ConsumeContext<TestIntegrationEvent>>();
        context.SetupGet(c => c.Message).Returns(evt);
        return context;
    }

    [Fact]
    public async Task Consume_RunsHandlersAndMarksProcessed_WhenNotYetProcessed()
    {
        var evt = new TestIntegrationEvent();
        var handler = new Mock<IIntegrationEventHandler<TestIntegrationEvent>>();
        var inbox = new Mock<IInboxStore>();
        inbox.Setup(x => x.AlreadyProcessedAsync(evt.MessageId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var sut = new IntegrationEventConsumer<TestIntegrationEvent>(
            [handler.Object], inbox.Object, Mock.Of<ILogger<IntegrationEventConsumer<TestIntegrationEvent>>>());

        await sut.Consume(ContextFor(evt).Object);

        handler.Verify(x => x.HandleAsync(evt, It.IsAny<CancellationToken>()), Times.Once);
        inbox.Verify(x => x.MarkProcessedAsync(evt.MessageId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_SkipsHandlersAndDoesNotRecord_WhenAlreadyProcessed()
    {
        var evt = new TestIntegrationEvent();
        var handler = new Mock<IIntegrationEventHandler<TestIntegrationEvent>>();
        var inbox = new Mock<IInboxStore>();
        inbox.Setup(x => x.AlreadyProcessedAsync(evt.MessageId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var sut = new IntegrationEventConsumer<TestIntegrationEvent>(
            [handler.Object], inbox.Object, Mock.Of<ILogger<IntegrationEventConsumer<TestIntegrationEvent>>>());

        await sut.Consume(ContextFor(evt).Object);

        handler.Verify(x => x.HandleAsync(It.IsAny<TestIntegrationEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        inbox.Verify(x => x.MarkProcessedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
