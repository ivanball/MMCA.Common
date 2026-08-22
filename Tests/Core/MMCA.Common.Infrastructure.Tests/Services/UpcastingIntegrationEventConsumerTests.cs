using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Services;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

/// <summary>
/// Tests for <see cref="UpcastingIntegrationEventConsumer{TEvent}"/> (ADR-090): the draining consumer
/// bound to a RETIRED contract. It dedups on the ORIGINAL message id, upcasts to the terminal
/// contract, dispatches to the handlers registered for THAT contract, rethrows a handler failure so
/// MassTransit can retry, and records the inbox row only after every handler succeeded.
/// <para>
/// Harness mirrors <see cref="IntegrationEventConsumerTests"/>: a mocked
/// <see cref="ConsumeContext{T}"/>, a mocked <see cref="IInboxStore"/>, and Moq handlers, plus a real
/// service provider because this consumer resolves its handlers non-generically at runtime. The V1/V2
/// contracts live in this TEST assembly only.
/// </para>
/// </summary>
public sealed class UpcastingIntegrationEventConsumerTests
{
    // ── Sample contracts: retired V1 and its successor V2 ──
    public sealed record class RetiredOrderPlaced(string Sku) : BaseIntegrationEvent;

    public sealed record class OrderPlacedV2(string Sku, string Source) : BaseIntegrationEvent
    {
        public override int SchemaVersion => 2;
    }

    private sealed class RetiredToV2Upcaster : IEventUpcaster<RetiredOrderPlaced, OrderPlacedV2>
    {
        public OrderPlacedV2 Upcast(RetiredOrderPlaced integrationEvent) =>
            new(integrationEvent.Sku, "upcasted");
    }

    private static Mock<ConsumeContext<RetiredOrderPlaced>> ContextFor(RetiredOrderPlaced evt)
    {
        var context = new Mock<ConsumeContext<RetiredOrderPlaced>>();
        context.SetupGet(c => c.Message).Returns(evt);
        return context;
    }

    private static Mock<IInboxStore> InboxFor(RetiredOrderPlaced evt, bool alreadyProcessed = false)
    {
        var inbox = new Mock<IInboxStore>();
        inbox.Setup(x => x.AlreadyProcessedAsync(evt.MessageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alreadyProcessed);
        return inbox;
    }

    private static UpcastingIntegrationEventConsumer<RetiredOrderPlaced> CreateSut(
        IServiceProvider provider,
        IInboxStore inbox,
        params IEventUpcaster[] upcasters) =>
        new(new EventUpcasterRegistry(upcasters),
            provider,
            inbox,
            Mock.Of<ILogger<UpcastingIntegrationEventConsumer<RetiredOrderPlaced>>>());

    // ── Upcast + dispatch: the terminal handler sees the upcasted instance, original envelope intact ──
    [Fact]
    public async Task Consume_WithUpcaster_InvokesTerminalHandlerWithUpcastedEventCarryingTheOriginalMessageId()
    {
        var evt = new RetiredOrderPlaced("SKU-1");
        var terminalHandler = new Mock<IIntegrationEventHandler<OrderPlacedV2>>();
        var retiredHandler = new Mock<IIntegrationEventHandler<RetiredOrderPlaced>>();
        var services = new ServiceCollection();
        services.AddSingleton(terminalHandler.Object);
        services.AddSingleton(retiredHandler.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var inbox = InboxFor(evt);

        var sut = CreateSut(provider, inbox.Object, new RetiredToV2Upcaster());

        await sut.Consume(ContextFor(evt).Object);

        terminalHandler.Verify(
            x => x.HandleAsync(
                It.Is<OrderPlacedV2>(e => e.Sku == "SKU-1" && e.Source == "upcasted" && e.MessageId == evt.MessageId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        retiredHandler.Verify(
            x => x.HandleAsync(It.IsAny<RetiredOrderPlaced>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Inbox dedup: a redelivery of an already-recorded message runs nothing ──
    [Fact]
    public async Task Consume_WhenAlreadyProcessed_SkipsHandlersAndDoesNotRecord()
    {
        var evt = new RetiredOrderPlaced("SKU-1");
        var terminalHandler = new Mock<IIntegrationEventHandler<OrderPlacedV2>>();
        var services = new ServiceCollection();
        services.AddSingleton(terminalHandler.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var inbox = InboxFor(evt, alreadyProcessed: true);

        var sut = CreateSut(provider, inbox.Object, new RetiredToV2Upcaster());

        await sut.Consume(ContextFor(evt).Object);

        terminalHandler.Verify(
            x => x.HandleAsync(It.IsAny<OrderPlacedV2>(), It.IsAny<CancellationToken>()),
            Times.Never);
        inbox.Verify(
            x => x.MarkProcessedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Handler failure: rethrown for the MassTransit retry policy, inbox left un-recorded ──
    [Fact]
    public async Task Consume_WhenHandlerThrows_RethrowsAndLeavesTheMessageUnrecorded()
    {
        var evt = new RetiredOrderPlaced("SKU-1");
        var failure = new InvalidOperationException("handler failed");
        var terminalHandler = new Mock<IIntegrationEventHandler<OrderPlacedV2>>();
        terminalHandler
            .Setup(x => x.HandleAsync(It.IsAny<OrderPlacedV2>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var services = new ServiceCollection();
        services.AddSingleton(terminalHandler.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var inbox = InboxFor(evt);

        var sut = CreateSut(provider, inbox.Object, new RetiredToV2Upcaster());

        Func<Task> act = () => sut.Consume(ContextFor(evt).Object);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(failure);
        inbox.Verify(
            x => x.MarkProcessedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Success: recorded under the ORIGINAL message id and the retired type name ──
    [Fact]
    public async Task Consume_OnSuccess_MarksProcessedWithTheOriginalMessageId()
    {
        var evt = new RetiredOrderPlaced("SKU-1");
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IIntegrationEventHandler<OrderPlacedV2>>());
        await using ServiceProvider provider = services.BuildServiceProvider();
        var inbox = InboxFor(evt);

        var sut = CreateSut(provider, inbox.Object, new RetiredToV2Upcaster());

        await sut.Consume(ContextFor(evt).Object);

        inbox.Verify(
            x => x.MarkProcessedAsync(evt.MessageId, nameof(RetiredOrderPlaced), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Degrade path: no upcaster registered means plain dispatch on the original contract ──
    [Fact]
    public async Task Consume_WithNoUpcasterRegistered_DispatchesToTheHandlersOfTheOriginalContract()
    {
        var evt = new RetiredOrderPlaced("SKU-1");
        var retiredHandler = new Mock<IIntegrationEventHandler<RetiredOrderPlaced>>();
        var services = new ServiceCollection();
        services.AddSingleton(retiredHandler.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var inbox = InboxFor(evt);

        var sut = CreateSut(provider, inbox.Object);

        await sut.Consume(ContextFor(evt).Object);

        retiredHandler.Verify(x => x.HandleAsync(evt, It.IsAny<CancellationToken>()), Times.Once);
        inbox.Verify(
            x => x.MarkProcessedAsync(evt.MessageId, nameof(RetiredOrderPlaced), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Nobody handles the terminal contract in this process: ack the message, do not retry it ──
    [Fact]
    public async Task Consume_WithNoHandlersForTheTerminalContract_CompletesAndMarksProcessed()
    {
        var evt = new RetiredOrderPlaced("SKU-1");
        var services = new ServiceCollection();
        await using ServiceProvider provider = services.BuildServiceProvider();
        var inbox = InboxFor(evt);

        var sut = CreateSut(provider, inbox.Object, new RetiredToV2Upcaster());

        Func<Task> act = () => sut.Consume(ContextFor(evt).Object);

        await act.Should().NotThrowAsync();
        inbox.Verify(
            x => x.MarkProcessedAsync(evt.MessageId, nameof(RetiredOrderPlaced), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Null guard ──
    [Fact]
    public async Task Consume_WithNullContext_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        await using ServiceProvider provider = services.BuildServiceProvider();

        var sut = CreateSut(provider, Mock.Of<IInboxStore>(), new RetiredToV2Upcaster());

        Func<Task> act = () => sut.Consume(null!);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("context");
    }
}
