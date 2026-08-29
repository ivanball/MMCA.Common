using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class IntegrationEventConsumerTests
{
    public sealed record class TestIntegrationEvent : BaseIntegrationEvent;

    [EventName(NamedEventIdentity)]
    public sealed record class NamedIntegrationEvent : BaseIntegrationEvent;

    private const string NamedEventIdentity = "MMCA.Tests.NamedIntegrationEvent.v1";

    private static Mock<ConsumeContext<TestIntegrationEvent>> ContextFor(TestIntegrationEvent evt)
    {
        var context = new Mock<ConsumeContext<TestIntegrationEvent>>();
        context.SetupGet(c => c.Message).Returns(evt);
        return context;
    }

    /// <summary>
    /// An inbox whose <c>TryBeginAsync</c> answers <paramref name="alreadyProcessed"/>. The consume
    /// path opens on TryBegin (which also stages the row in the handler's unit of work) and closes on
    /// CompleteAsync, so those are the two calls a test asserts on.
    /// </summary>
    private static Mock<IInboxStore> InboxFor(TestIntegrationEvent evt, bool alreadyProcessed = false)
    {
        var inbox = new Mock<IInboxStore>();
        inbox.Setup(x => x.TryBeginAsync(evt.MessageId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(!alreadyProcessed);
        return inbox;
    }

    [Fact]
    public async Task Consume_RunsHandlersAndCompletesTheInbox_WhenNotYetProcessed()
    {
        var evt = new TestIntegrationEvent();
        var handler = new Mock<IIntegrationEventHandler<TestIntegrationEvent>>();
        var inbox = InboxFor(evt);

        var sut = new IntegrationEventConsumer<TestIntegrationEvent>(
            [handler.Object], inbox.Object, Mock.Of<ILogger<IntegrationEventConsumer<TestIntegrationEvent>>>());

        await sut.Consume(ContextFor(evt).Object);

        handler.Verify(x => x.HandleAsync(evt, It.IsAny<CancellationToken>()), Times.Once);
        inbox.Verify(
            x => x.TryBeginAsync(evt.MessageId, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the inbox row is staged BEFORE the handlers run, so a handler's own save commits it atomically");
        inbox.Verify(x => x.CompleteAsync(evt.MessageId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_SkipsHandlersAndDoesNotRecord_WhenAlreadyProcessed()
    {
        var evt = new TestIntegrationEvent();
        var handler = new Mock<IIntegrationEventHandler<TestIntegrationEvent>>();
        var inbox = InboxFor(evt, alreadyProcessed: true);

        var sut = new IntegrationEventConsumer<TestIntegrationEvent>(
            [handler.Object], inbox.Object, Mock.Of<ILogger<IntegrationEventConsumer<TestIntegrationEvent>>>());

        await sut.Consume(ContextFor(evt).Object);

        handler.Verify(x => x.HandleAsync(It.IsAny<TestIntegrationEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        inbox.Verify(
            x => x.CompleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Consume_HandlerThrows_AbandonsTheStagedRowAndRethrows()
    {
        // The staged row must be discarded on the way out: left behind it would either poison the
        // scope's context with a rejected insert or make MassTransit's redelivery look like a
        // duplicate, silently swallowing the retry the throw is asking for.
        var evt = new TestIntegrationEvent();
        var handler = new Mock<IIntegrationEventHandler<TestIntegrationEvent>>();
        handler.Setup(x => x.HandleAsync(evt, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("handler blew up"));
        var inbox = InboxFor(evt);

        var sut = new IntegrationEventConsumer<TestIntegrationEvent>(
            [handler.Object], inbox.Object, Mock.Of<ILogger<IntegrationEventConsumer<TestIntegrationEvent>>>());

        var consume = async () => await sut.Consume(ContextFor(evt).Object);
        await consume.Should().ThrowAsync<InvalidOperationException>();

        inbox.Verify(x => x.Abandon(evt.MessageId), Times.Once);
        inbox.Verify(
            x => x.CompleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a failed consume must not record the message as processed");
    }

    [Fact]
    public async Task Consume_KeysTheInboxOnTheShortTypeName_WhenTheEventDoesNotDeclareOne()
    {
        // The default identity is unchanged, so rows written before [EventName] existed keep
        // matching and a redelivery is still recognised as a duplicate.
        var evt = new TestIntegrationEvent();
        var inbox = InboxFor(evt);

        var sut = new IntegrationEventConsumer<TestIntegrationEvent>(
            [], inbox.Object, Mock.Of<ILogger<IntegrationEventConsumer<TestIntegrationEvent>>>());

        await sut.Consume(ContextFor(evt).Object);

        inbox.Verify(
            x => x.TryBeginAsync(evt.MessageId, nameof(TestIntegrationEvent), It.IsAny<CancellationToken>()),
            Times.Once);
        inbox.Verify(
            x => x.CompleteAsync(evt.MessageId, nameof(TestIntegrationEvent), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Consume_KeysTheInboxOnTheDeclaredName_WhenTheEventCarriesTheAttribute()
    {
        var evt = new NamedIntegrationEvent();
        var inbox = new Mock<IInboxStore>();
        inbox.Setup(x => x.TryBeginAsync(evt.MessageId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var context = new Mock<ConsumeContext<NamedIntegrationEvent>>();
        context.SetupGet(c => c.Message).Returns(evt);

        var sut = new IntegrationEventConsumer<NamedIntegrationEvent>(
            [], inbox.Object, Mock.Of<ILogger<IntegrationEventConsumer<NamedIntegrationEvent>>>());

        await sut.Consume(context.Object);

        inbox.Verify(
            x => x.TryBeginAsync(evt.MessageId, NamedEventIdentity, It.IsAny<CancellationToken>()),
            Times.Once,
            "the declared identity is what survives a rename, so it is what the inbox row must hold");
        inbox.Verify(
            x => x.CompleteAsync(evt.MessageId, NamedEventIdentity, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
