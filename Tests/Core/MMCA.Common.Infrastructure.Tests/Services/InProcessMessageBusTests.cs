using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Services;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

/// <summary>
/// Tests for <see cref="InProcessMessageBus"/>: the monolith-mode <c>IMessageBus</c> that
/// forwards straight to <see cref="IDomainEventDispatcher"/> without touching the outbox.
/// Covers the adapter contract against a mocked dispatcher, plus end-to-end dispatch through
/// the real <see cref="DomainEventDispatcher"/> with DI-registered handlers.
/// </summary>
public sealed class InProcessMessageBusTests
{
    public sealed record class TestIntegrationEvent : BaseIntegrationEvent;

    // ── Mocks ──
    private sealed record Mocks(Mock<IDomainEventDispatcher> Dispatcher);

    // ── Factory ──
    private static (InProcessMessageBus Sut, Mocks Mocks) CreateSut()
    {
        var dispatcher = new Mock<IDomainEventDispatcher>();
        return (new InProcessMessageBus(dispatcher.Object), new Mocks(dispatcher));
    }

    // ── Null guard: single event ──
    [Fact]
    public async Task PublishAsync_NullEvent_ThrowsArgumentNullException()
    {
        var (sut, _) = CreateSut();

        Func<Task> act = () => sut.PublishAsync((IIntegrationEvent)null!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("integrationEvent");
    }

    // ── Null guard: batch overload ──
    [Fact]
    public async Task PublishAsync_NullEventCollection_ThrowsArgumentNullException()
    {
        var (sut, _) = CreateSut();

        Func<Task> act = () => sut.PublishAsync((IEnumerable<IIntegrationEvent>)null!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("integrationEvents");
    }

    // ── Single event: dispatched once as a single-element collection ──
    [Fact]
    public async Task PublishAsync_SingleEvent_DispatchesCollectionContainingOnlyThatEvent()
    {
        var (sut, mocks) = CreateSut();
        var integrationEvent = new TestIntegrationEvent();

        await sut.PublishAsync(integrationEvent, CancellationToken.None);

        mocks.Dispatcher.Verify(
            d => d.DispatchAsync(
                It.Is<IEnumerable<IDomainEvent>>(events =>
                    events.Take(2).Count() == 1 && events.Contains(integrationEvent)),
                CancellationToken.None),
            Times.Once);
    }

    // ── Single event: exact token forwarded to the dispatcher ──
    [Fact]
    public async Task PublishAsync_SingleEvent_PropagatesCancellationToken()
    {
        var (sut, mocks) = CreateSut();
        using var cts = new CancellationTokenSource();

        await sut.PublishAsync(new TestIntegrationEvent(), cts.Token);

        mocks.Dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), cts.Token),
            Times.Once);
    }

    // ── Batch: the collection is forwarded as-is in ONE dispatch call (not per event) ──
    [Fact]
    public async Task PublishBatch_ForwardsSameCollectionInSingleDispatchCall()
    {
        var (sut, mocks) = CreateSut();
        IIntegrationEvent[] events = [new TestIntegrationEvent(), new TestIntegrationEvent()];
        using var cts = new CancellationTokenSource();

        await sut.PublishAsync(events, cts.Token);

        mocks.Dispatcher.Verify(d => d.DispatchAsync(events, cts.Token), Times.Once);
        mocks.Dispatcher.VerifyNoOtherCalls();
    }

    // ── Failure: a dispatcher exception propagates unwrapped ──
    [Fact]
    public async Task PublishAsync_WhenDispatcherThrows_PropagatesSameException()
    {
        var (sut, mocks) = CreateSut();
        var handlerFailure = new InvalidOperationException("handler failed");
        mocks.Dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException(handlerFailure));

        Func<Task> act = () => sut.PublishAsync(new TestIntegrationEvent(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(handlerFailure);
    }

    // ── Real dispatcher: all DI-registered handlers run, domain handlers before integration
    //    handlers, each group in registration order (the dispatcher awaits sequentially) ──
    [Fact]
    public async Task PublishAsync_WithRealDispatcherAndDiHandlers_InvokesAllHandlersInDeterministicOrder()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<TestIntegrationEvent>>(new RecordingDomainHandler(log, "domain"));
        services.AddSingleton<IIntegrationEventHandler<TestIntegrationEvent>>(new RecordingIntegrationHandler(log, "integration-1"));
        services.AddSingleton<IIntegrationEventHandler<TestIntegrationEvent>>(new RecordingIntegrationHandler(log, "integration-2"));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);
        var sut = new InProcessMessageBus(dispatcher);

        await sut.PublishAsync(new TestIntegrationEvent(), CancellationToken.None);

        log.Should().Equal("domain", "integration-1", "integration-2");
    }

    // ── Real dispatcher: no registered handlers is a silent no-op, not an error ──
    [Fact]
    public async Task PublishAsync_WithRealDispatcherAndNoHandlers_CompletesWithoutError()
    {
        var services = new ServiceCollection();
        await using ServiceProvider provider = services.BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);
        var sut = new InProcessMessageBus(dispatcher);

        Func<Task> act = () => sut.PublishAsync(new TestIntegrationEvent(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── Test handlers ──
    private sealed class RecordingDomainHandler(List<string> log, string name) : IDomainEventHandler<TestIntegrationEvent>
    {
        public Task HandleAsync(TestIntegrationEvent domainEvent, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingIntegrationHandler(List<string> log, string name) : IIntegrationEventHandler<TestIntegrationEvent>
    {
        public Task HandleAsync(TestIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            return Task.CompletedTask;
        }
    }
}
