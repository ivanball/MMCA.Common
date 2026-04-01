using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
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
}
