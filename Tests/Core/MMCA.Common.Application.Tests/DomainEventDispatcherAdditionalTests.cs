using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Services;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Tests;

public sealed class DomainEventDispatcherAdditionalTests
{
    // ── Integration event dispatch ──
    private sealed record TestIntegrationEvent(string Data) : BaseIntegrationEvent;

    private sealed class TestIntegrationEventHandler : IIntegrationEventHandler<TestIntegrationEvent>
    {
        public List<TestIntegrationEvent> HandledEvents { get; } = [];

        public Task HandleAsync(TestIntegrationEvent domainEvent, CancellationToken cancellationToken = default)
        {
            HandledEvents.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class TestDomainEventHandlerForIntegration : IDomainEventHandler<TestIntegrationEvent>
    {
        public List<TestIntegrationEvent> HandledEvents { get; } = [];

        public Task HandleAsync(TestIntegrationEvent domainEvent, CancellationToken cancellationToken = default)
        {
            HandledEvents.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DispatchAsync_IntegrationEvent_DispatchesToBothDomainAndIntegrationHandlers()
    {
        var domainHandler = new TestDomainEventHandlerForIntegration();
        var integrationHandler = new TestIntegrationEventHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<TestIntegrationEvent>>(domainHandler);
        services.AddSingleton<IIntegrationEventHandler<TestIntegrationEvent>>(integrationHandler);
        ServiceProvider provider = services.BuildServiceProvider();

        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);
        var evt = new TestIntegrationEvent("integration-data");

        await dispatcher.DispatchAsync([evt]);

        domainHandler.HandledEvents.Should().ContainSingle()
            .Which.Data.Should().Be("integration-data");
        integrationHandler.HandledEvents.Should().ContainSingle()
            .Which.Data.Should().Be("integration-data");
    }

    [Fact]
    public async Task DispatchAsync_IntegrationEvent_WithOnlyDomainHandler_DoesNotThrow()
    {
        var domainHandler = new TestDomainEventHandlerForIntegration();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<TestIntegrationEvent>>(domainHandler);
        ServiceProvider provider = services.BuildServiceProvider();

        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);

        await FluentActions.Invoking(() => dispatcher.DispatchAsync([new TestIntegrationEvent("data")]))
            .Should().NotThrowAsync();

        domainHandler.HandledEvents.Should().ContainSingle();
    }

    // ── Multiple handlers for same event ──
    private sealed record MultiHandlerEvent(string Data) : BaseDomainEvent;

    private sealed class MultiHandlerEventHandler1 : IDomainEventHandler<MultiHandlerEvent>
    {
        public bool Called { get; private set; }

        public Task HandleAsync(MultiHandlerEvent domainEvent, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    private sealed class MultiHandlerEventHandler2 : IDomainEventHandler<MultiHandlerEvent>
    {
        public bool Called { get; private set; }

        public Task HandleAsync(MultiHandlerEvent domainEvent, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DispatchAsync_WithMultipleHandlers_InvokesAll()
    {
        var handler1 = new MultiHandlerEventHandler1();
        var handler2 = new MultiHandlerEventHandler2();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<MultiHandlerEvent>>(handler1);
        services.AddSingleton<IDomainEventHandler<MultiHandlerEvent>>(handler2);
        ServiceProvider provider = services.BuildServiceProvider();

        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);

        await dispatcher.DispatchAsync([new MultiHandlerEvent("data")]);

        handler1.Called.Should().BeTrue();
        handler2.Called.Should().BeTrue();
    }

    // ── Upcasted integration-event dispatch (ADR-090) ──
    // The tests above are the no-registry regression guard: with no IEventUpcasterRegistry in the
    // provider the dispatcher behaves exactly as it always did. These add the registry.
    private sealed record RetiredEvent(string FullName) : BaseIntegrationEvent;

    private sealed record SuccessorEvent(string FullName) : BaseIntegrationEvent
    {
        public override int SchemaVersion => 2;
    }

    private sealed class RetiredToSuccessorUpcaster : IEventUpcaster<RetiredEvent, SuccessorEvent>
    {
        public SuccessorEvent Upcast(RetiredEvent integrationEvent) => new(integrationEvent.FullName);
    }

    private sealed class RecordingIntegrationHandler<TEvent> : IIntegrationEventHandler<TEvent>
        where TEvent : class, IIntegrationEvent
    {
        public List<TEvent> HandledEvents { get; } = [];

        public Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            HandledEvents.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDomainHandlerForRetired : IDomainEventHandler<RetiredEvent>
    {
        public List<RetiredEvent> HandledEvents { get; } = [];

        public Task HandleAsync(RetiredEvent domainEvent, CancellationToken cancellationToken = default)
        {
            HandledEvents.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DispatchAsync_WithRegisteredUpcaster_InvokesTheSuccessorHandlerOnly()
    {
        var successorHandler = new RecordingIntegrationHandler<SuccessorEvent>();
        var retiredHandler = new RecordingIntegrationHandler<RetiredEvent>();
        var services = new ServiceCollection();
        services.AddSingleton<IIntegrationEventHandler<SuccessorEvent>>(successorHandler);
        services.AddSingleton<IIntegrationEventHandler<RetiredEvent>>(retiredHandler);
        services.AddSingleton<IEventUpcasterRegistry>(new EventUpcasterRegistry([new RetiredToSuccessorUpcaster()]));
        ServiceProvider provider = services.BuildServiceProvider();

        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);
        var retired = new RetiredEvent("Ada Lovelace");

        await dispatcher.DispatchAsync([retired]);

        successorHandler.HandledEvents.Should().ContainSingle()
            .Which.FullName.Should().Be("Ada Lovelace");
        successorHandler.HandledEvents[0].MessageId.Should().Be(retired.MessageId,
            "the registry preserves the envelope across the hop");
        retiredHandler.HandledEvents.Should().BeEmpty(
            "handlers are written once, against the newest contract: the retired-type handler must not also fire");
    }

    [Fact]
    public async Task DispatchAsync_WithRegisteredUpcaster_StillGivesDomainHandlersTheOriginalInstance()
    {
        var domainHandler = new RecordingDomainHandlerForRetired();
        var successorHandler = new RecordingIntegrationHandler<SuccessorEvent>();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<RetiredEvent>>(domainHandler);
        services.AddSingleton<IIntegrationEventHandler<SuccessorEvent>>(successorHandler);
        services.AddSingleton<IEventUpcasterRegistry>(new EventUpcasterRegistry([new RetiredToSuccessorUpcaster()]));
        ServiceProvider provider = services.BuildServiceProvider();

        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);
        var retired = new RetiredEvent("Ada Lovelace");

        await dispatcher.DispatchAsync([retired]);

        domainHandler.HandledEvents.Should().ContainSingle()
            .Which.Should().BeSameAs(retired, "intra-module domain handlers keep the original type and instance");
        successorHandler.HandledEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task DispatchAsync_WithEmptyRegistry_LeavesTheOriginalContractInPlace()
    {
        var retiredHandler = new RecordingIntegrationHandler<RetiredEvent>();
        var services = new ServiceCollection();
        services.AddSingleton<IIntegrationEventHandler<RetiredEvent>>(retiredHandler);
        services.AddSingleton<IEventUpcasterRegistry>(new EventUpcasterRegistry([]));
        ServiceProvider provider = services.BuildServiceProvider();

        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);
        var retired = new RetiredEvent("Ada Lovelace");

        await dispatcher.DispatchAsync([retired]);

        retiredHandler.HandledEvents.Should().ContainSingle()
            .Which.Should().BeSameAs(retired);
    }
}
