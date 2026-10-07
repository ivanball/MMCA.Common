using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using Moq;
using RoutingTests = MMCA.Common.Infrastructure.Tests.Persistence.Interceptors.DomainEventSaveChangesInterceptorOutboxRoutingTests;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Interceptors;

/// <summary>
/// A local (non-integration) domain event's outbox row is a safety net for the in-process dispatch
/// that runs right after the save. With more than one replica polling, an unleased row is fair game
/// for another replica's <see cref="OutboxProcessor"/> while that dispatch is still running, which
/// delivers the event twice. So the interceptor inserts local rows already leased (a future
/// <see cref="OutboxMessage.LockedUntil"/> and a fresh <see cref="OutboxMessage.LockToken"/>), and
/// when the in-process dispatch fails it clears the lease before signalling, so the processor retries
/// promptly instead of after the full lease. Integration rows stay unleased: nothing in-process
/// delivers them.
/// </summary>
public sealed class DomainEventSaveChangesInterceptorLocalLeaseTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero));
    private readonly Mock<IDomainEventDispatcher> _dispatcher = new();

    [Fact]
    public async Task SaveChangesAsync_LocalEvent_InsertsItsOutboxRowAlreadyLeased()
    {
        await using var context = CreateContext();
        (DateTime? LockedUntil, Guid? LockToken)? atDispatch = null;
        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => atDispatch = LeaseOf(context))
            .Returns(Task.CompletedTask);

        var entity = new RoutingTests.TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new RoutingTests.TestLocalEvent("local"));
        context.TestAggregates.Add(entity);

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        atDispatch.Should().NotBeNull("the in-process dispatch ran");
        atDispatch!.Value.LockedUntil.Should().NotBeNull("a poller must skip the row while the in-process dispatch runs");
        atDispatch.Value.LockedUntil!.Value.Should().BeAfter(_clock.GetUtcNow().UtcDateTime);
        atDispatch.Value.LockToken.Should().NotBeNull("the lease carries a token so its release can be guarded");
    }

    [Fact]
    public async Task SaveChangesAsync_WhenTheInProcessDispatchFails_ClearsTheLeaseSoTheProcessorRetriesPromptly()
    {
        await using var context = CreateContext();
        (DateTime? LockedUntil, Guid? LockToken)? atDispatch = null;
        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => atDispatch = LeaseOf(context))
            .ThrowsAsync(new InvalidOperationException("handler failed"));

        var entity = new RoutingTests.TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new RoutingTests.TestLocalEvent("local"));
        context.TestAggregates.Add(entity);

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        atDispatch!.Value.LockedUntil.Should().NotBeNull("the row was inserted leased");
        OutboxMessage row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        row.ProcessedOn.Should().BeNull("the event was not delivered");
        row.LockedUntil.Should().BeNull("a failed in-process dispatch hands the row straight to the processor, not after the lease");
    }

    [Fact]
    public async Task SaveChangesAsync_IntegrationEvent_LeavesItsOutboxRowUnleased()
    {
        await using var context = CreateContext();

        var entity = new RoutingTests.TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new RoutingTests.TestIntegrationEvent());
        context.TestAggregates.Add(entity);

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        OutboxMessage row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        row.LockedUntil.Should().BeNull("only the processor delivers an integration event, so nothing may hold it back");
    }

    private static (DateTime? LockedUntil, Guid? LockToken) LeaseOf(DbContext context)
    {
        var row = context.ChangeTracker.Entries<OutboxMessage>().Single().Entity;
        return (row.LockedUntil, row.LockToken);
    }

    private RoutingTests.OutboxRoutingTestDbContext CreateContext() =>
        RoutingTests.OutboxRoutingTestDbContext.Create(new DomainEventSaveChangesInterceptor(
            _dispatcher.Object,
            NullLogger<DomainEventSaveChangesInterceptor>.Instance,
            Mock.Of<IOutboxSignal>(),
            _clock));
}
