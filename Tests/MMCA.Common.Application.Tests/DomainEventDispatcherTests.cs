using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Services;
using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Application.Tests;

public class DomainEventDispatcherTests
{
    private sealed record TestEvent(string Data) : BaseDomainEvent;

    private sealed class TestEventHandler : IDomainEventHandler<TestEvent>
    {
        public List<TestEvent> HandledEvents { get; } = [];

        public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken = default)
        {
            HandledEvents.Add(domainEvent);
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
}
