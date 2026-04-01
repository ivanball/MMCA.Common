using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Services;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Tests;

public class DomainEventDispatcherTests
{
    private sealed record TestEvent(string Data) : BaseDomainEvent;

    private sealed record TestIntegrationEvent(string Data) : BaseDomainEvent, IIntegrationEvent;

    private sealed class TestEventHandler : IDomainEventHandler<TestEvent>
    {
        public List<TestEvent> HandledEvents { get; } = [];

        public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken = default)
        {
            HandledEvents.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class TestIntegrationEventDomainHandler : IDomainEventHandler<TestIntegrationEvent>
    {
        public List<TestIntegrationEvent> HandledEvents { get; } = [];

        public Task HandleAsync(TestIntegrationEvent domainEvent, CancellationToken cancellationToken = default)
        {
            HandledEvents.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class TestIntegrationEventHandler : IIntegrationEventHandler<TestIntegrationEvent>
    {
        public List<TestIntegrationEvent> HandledEvents { get; } = [];

        public Task HandleAsync(TestIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            HandledEvents.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DispatchAsync_WithRegisteredHandler_InvokesHandler()
    {
        var handler = new TestEventHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<TestEvent>>(handler);
        var provider = services.BuildServiceProvider();

        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);
        var evt = new TestEvent("test-data");

        await dispatcher.DispatchAsync([evt]);

        handler.HandledEvents.Should().ContainSingle()
            .Which.Data.Should().Be("test-data");
    }

    [Fact]
    public async Task DispatchAsync_WithMultipleEvents_InvokesHandlerForEach()
    {
        var handler = new TestEventHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<TestEvent>>(handler);
        var provider = services.BuildServiceProvider();

        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);

        await dispatcher.DispatchAsync([new TestEvent("A"), new TestEvent("B")]);

        handler.HandledEvents.Should().HaveCount(2);
    }

    [Fact]
    public async Task DispatchAsync_WithNoHandlers_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);

        await FluentActions.Invoking(() => dispatcher.DispatchAsync([new TestEvent("orphan")]))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task DispatchAsync_WithEmptyEvents_DoesNothing()
    {
        var handler = new TestEventHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<TestEvent>>(handler);
        var provider = services.BuildServiceProvider();

        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);

        await dispatcher.DispatchAsync([]);

        handler.HandledEvents.Should().BeEmpty();
    }

    [Fact]
    public void DispatchAsync_WithNullEvents_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);

        FluentActions.Invoking(() => dispatcher.DispatchAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullServiceProvider_ThrowsArgumentNullException() =>
        FluentActions.Invoking(() => new DomainEventDispatcher(null!, NullLogger<DomainEventDispatcher>.Instance))
            .Should().Throw<ArgumentNullException>();

    // ── Integration event dispatching ──
    [Fact]
    public async Task DispatchAsync_WithIntegrationEvent_DispatchesToBothDomainAndIntegrationHandlers()
    {
        var domainHandler = new TestIntegrationEventDomainHandler();
        var integrationHandler = new TestIntegrationEventHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<TestIntegrationEvent>>(domainHandler);
        services.AddSingleton<IIntegrationEventHandler<TestIntegrationEvent>>(integrationHandler);
        var provider = services.BuildServiceProvider();

        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);
        var evt = new TestIntegrationEvent("cross-module-data");

        await dispatcher.DispatchAsync([evt]);

        domainHandler.HandledEvents.Should().ContainSingle()
            .Which.Data.Should().Be("cross-module-data");
        integrationHandler.HandledEvents.Should().ContainSingle()
            .Which.Data.Should().Be("cross-module-data");
    }

    [Fact]
    public async Task DispatchAsync_WithIntegrationEvent_NoIntegrationHandlerRegistered_DoesNotThrow()
    {
        var domainHandler = new TestIntegrationEventDomainHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<TestIntegrationEvent>>(domainHandler);
        var provider = services.BuildServiceProvider();

        var dispatcher = new DomainEventDispatcher(provider, NullLogger<DomainEventDispatcher>.Instance);
        var evt = new TestIntegrationEvent("no-integration-handler");

        await dispatcher.DispatchAsync([evt]);

        domainHandler.HandledEvents.Should().ContainSingle();
    }
}
