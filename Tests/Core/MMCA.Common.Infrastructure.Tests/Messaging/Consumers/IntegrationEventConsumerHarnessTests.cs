using AwesomeAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging.Consumers;
using MMCA.Common.Infrastructure.Persistence.Inbox;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Consumers;

/// <summary>
/// The integration-event consumers driven through a REAL MassTransit bus (the in-memory transport
/// behind <c>AddMassTransitTestHarness</c>) rather than by calling <c>Consume</c> directly.
/// <para>
/// IntegrationEventConsumerTests already pins the consumer's own logic; what it cannot see is the
/// wiring around it: that <c>RegisterIntegrationEventConsumer&lt;TEvent&gt;</c> actually binds the
/// generic consumer to the event's endpoint, that a published event serializes and deserializes back
/// into the same <c>MessageId</c> the inbox dedups on, and that a handler exception really does turn
/// into the <c>Fault&lt;TEvent&gt;</c> message FaultIntegrationEventConsumer subscribes to. Those are
/// broker behaviors, so they need a bus.
/// </para>
/// <para>
/// In-memory transport only: no Docker, no broker, so this tier stays in the ordinary unit run.
/// </para>
/// </summary>
public sealed class IntegrationEventConsumerHarnessTests
{
    public sealed record class HarnessTestEvent : BaseIntegrationEvent;

    public sealed record class HarnessFaultingEvent : BaseIntegrationEvent;

    [Fact]
    public async Task PublishedEvent_ReachesTheRegisteredIntegrationEventHandler()
    {
        var handler = new RecordingHandler<HarnessTestEvent>();
        var inbox = new RecordingInboxStore();

        await using var provider = BuildProvider(
            services => services
                .AddSingleton<IIntegrationEventHandler<HarnessTestEvent>>(handler)
                .AddSingleton<IInboxStore>(inbox),
            bus => bus.RegisterIntegrationEventConsumer<HarnessTestEvent>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var published = new HarnessTestEvent();
        await harness.Bus.Publish(published);

        (await harness.Consumed.Any<HarnessTestEvent>()).Should().BeTrue();
        (await harness.GetConsumerHarness<IntegrationEventConsumer<HarnessTestEvent>>()
            .Consumed.Any<HarnessTestEvent>())
            .Should().BeTrue("RegisterIntegrationEventConsumer must bind the generic consumer to the event");

        handler.Handled.Should().ContainSingle(
            "the broker-delivered message is routed to the plain IIntegrationEventHandler contract");
        handler.Handled[0].MessageId.Should().Be(
            published.MessageId,
            "MessageId travels in the payload, so the inbox dedups on the publisher's id and not on a transport id");
        inbox.Completed.Should().Be(1, "a successful consume records the message as processed");
    }

    [Fact]
    public async Task HandlerFailure_SurfacesAsAFaultConsumedByTheFaultConsumer()
    {
        var inbox = new RecordingInboxStore();

        await using var provider = BuildProvider(
            services => services
                .AddSingleton<IIntegrationEventHandler<HarnessFaultingEvent>>(new ThrowingHandler<HarnessFaultingEvent>())
                .AddSingleton<IInboxStore>(inbox),
            bus => bus.RegisterIntegrationEventConsumer<HarnessFaultingEvent>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new HarnessFaultingEvent());

        (await harness.Published.Any<Fault<HarnessFaultingEvent>>()).Should().BeTrue(
            "the consumer rethrows, so MassTransit publishes the fault instead of silently acking");
        (await harness.GetConsumerHarness<FaultIntegrationEventConsumer<HarnessFaultingEvent>>()
            .Consumed.Any<Fault<HarnessFaultingEvent>>())
            .Should().BeTrue("the fault consumer registered alongside the event is what turns that fault into a signal");

        inbox.Abandoned.Should().Be(1, "the staged row is discarded so the redelivery is not mistaken for a duplicate");
        inbox.Completed.Should().Be(0, "a failed consume must not record the message as processed");
    }

    [Fact]
    public async Task DuplicateDeliveryOfTheSameMessageId_IsSuppressedByTheInbox()
    {
        var handler = new RecordingHandler<HarnessTestEvent>();
        var inbox = new RecordingInboxStore();

        await using var provider = BuildProvider(
            services => services
                .AddSingleton<IIntegrationEventHandler<HarnessTestEvent>>(handler)
                .AddSingleton<IInboxStore>(inbox),
            bus => bus.RegisterIntegrationEventConsumer<HarnessTestEvent>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // The SAME instance twice: at-least-once delivery is a redelivery of one message, so both
        // copies carry one MessageId. That id, not the transport's, is what the inbox claims.
        var published = new HarnessTestEvent();
        IConsumerTestHarness<IntegrationEventConsumer<HarnessTestEvent>> consumer =
            harness.GetConsumerHarness<IntegrationEventConsumer<HarnessTestEvent>>();

        await harness.Bus.Publish(published);

        // The redelivery is published only once the first consume has finished (the consumer harness
        // records a message after Consume returns), so "exactly one handler call" is a claim about a
        // settled state rather than a race with the first delivery.
        (await consumer.Consumed.Any<HarnessTestEvent>()).Should().BeTrue();
        await harness.Bus.Publish(published);
        await inbox.SecondDelivery.WaitAsync(TimeSpan.FromSeconds(10));

        inbox.Deliveries.Should().Be(2, "the broker delivered the message twice, which is the case being defended against");
        handler.Handled.Should().ContainSingle("the duplicate is suppressed before the handlers run");
        inbox.Completed.Should().Be(1, "only the delivery that actually ran the handlers closes the inbox row");
    }

    private static ServiceProvider BuildProvider(
        Action<IServiceCollection> configureServices,
        Action<IBusRegistrationConfigurator> configureBus)
    {
        var services = new ServiceCollection();

        // The consumers take an ILogger<T>, so the logging services are part of the contract under
        // test, not scaffolding: without them the consumer cannot even be constructed.
        services.AddLogging();
        configureServices(services);
        services.AddMassTransitTestHarness(configureBus);

        return services.BuildServiceProvider();
    }

    /// <summary>Captures every event the bus routed to the handler contract.</summary>
    /// <typeparam name="TEvent">The integration event type.</typeparam>
    private sealed class RecordingHandler<TEvent> : IIntegrationEventHandler<TEvent>
        where TEvent : class, IIntegrationEvent
    {
        private readonly System.Threading.Lock _gate = new();
        private readonly List<TEvent> _handled = [];

        /// <summary>A snapshot, because the bus writes this list from its own delivery threads.</summary>
        public IReadOnlyList<TEvent> Handled
        {
            get
            {
                lock (_gate)
                {
                    return [.. _handled];
                }
            }
        }

        public Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _handled.Add(integrationEvent);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Stands in for a handler whose work failed, which is what produces the fault.</summary>
    /// <typeparam name="TEvent">The integration event type.</typeparam>
    private sealed class ThrowingHandler<TEvent> : IIntegrationEventHandler<TEvent>
        where TEvent : class, IIntegrationEvent
    {
        public Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("handler blew up on the bus");
    }

    /// <summary>
    /// An in-memory inbox that claims a message id ATOMICALLY inside TryBeginAsync. The default
    /// interface implementation answers TryBegin from a separate AlreadyProcessed read, which two
    /// concurrent deliveries of one message can both pass; claiming under the lock makes the
    /// duplicate-suppression assertion independent of how the transport schedules the two copies.
    /// </summary>
    private sealed class RecordingInboxStore : IInboxStore
    {
        private readonly System.Threading.Lock _gate = new();
        private readonly HashSet<Guid> _processed = [];
        private readonly TaskCompletionSource _secondDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deliveries;
        private int _completed;
        private int _abandoned;

        /// <summary>Completes once a SECOND delivery has reached the inbox, so a test can stop waiting.</summary>
        public Task SecondDelivery => _secondDelivery.Task;

        /// <summary>Deliveries that reached the inbox, duplicates included.</summary>
        public int Deliveries => Volatile.Read(ref _deliveries);

        /// <summary>Deliveries that ran to completion and were recorded.</summary>
        public int Completed => Volatile.Read(ref _completed);

        /// <summary>Deliveries whose staged row was discarded after a handler failure.</summary>
        public int Abandoned => Volatile.Read(ref _abandoned);

        public Task<bool> AlreadyProcessedAsync(Guid messageId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(_processed.Contains(messageId));
            }
        }

        public Task MarkProcessedAsync(Guid messageId, string eventType, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _processed.Add(messageId);
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryBeginAsync(Guid messageId, string eventType, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _deliveries) >= 2)
            {
                _secondDelivery.TrySetResult();
            }

            bool claimed;
            lock (_gate)
            {
                claimed = _processed.Add(messageId);
            }

            return Task.FromResult(claimed);
        }

        public Task CompleteAsync(Guid messageId, string eventType, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _completed);
            return Task.CompletedTask;
        }

        public bool Abandon(Guid messageId)
        {
            Interlocked.Increment(ref _abandoned);

            lock (_gate)
            {
                _processed.Remove(messageId);
            }

            return true;
        }
    }
}
