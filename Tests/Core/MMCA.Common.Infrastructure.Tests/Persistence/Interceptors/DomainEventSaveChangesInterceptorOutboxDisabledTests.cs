using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Interceptors;

/// <summary>
/// The outbox-disabled routing of <see cref="DomainEventSaveChangesInterceptor"/>: with
/// <c>MessageBus:EnableOutbox=false</c> (the in-process default) the context still supports the
/// outbox table, but this host writes no rows and dispatches every captured event in-process. That
/// is the same branch a context without outbox support has always taken; these tests pin that a
/// disabled outbox loses no event, which is the one way the optimisation could go wrong.
/// </summary>
public sealed class DomainEventSaveChangesInterceptorOutboxDisabledTests : IDisposable
{
    private readonly Mock<IDomainEventDispatcher> _mockDispatcher = new();
    private readonly Mock<IOutboxSignal> _mockSignal = new();
    private readonly RecordingLogger _logger = new();
    private readonly DomainEventSaveChangesInterceptorOutboxRoutingTests.OutboxRoutingTestDbContext _dbContext;

    public DomainEventSaveChangesInterceptorOutboxDisabledTests()
    {
        var interceptor = new DomainEventSaveChangesInterceptor(
            _mockDispatcher.Object,
            _logger,
            _mockSignal.Object,
            timeProvider: TimeProvider.System,
            Options.Create(new MessageBusSettings { EnableOutbox = false }));
        _dbContext = DomainEventSaveChangesInterceptorOutboxRoutingTests.OutboxRoutingTestDbContext.Create(interceptor);
    }

    public void Dispose() => _dbContext.Dispose();

    private Task<List<OutboxMessage>> GetOutboxRowsAsync() =>
        _dbContext.Set<OutboxMessage>().AsNoTracking().ToListAsync();

    [Fact]
    public async Task SaveChangesAsync_LocalEvent_DispatchesInProcessAndWritesNoRow()
    {
        var entity = new DomainEventSaveChangesInterceptorOutboxRoutingTests.TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new DomainEventSaveChangesInterceptorOutboxRoutingTests.TestLocalEvent("local"));
        _dbContext.TestAggregates.Add(entity);

        await _dbContext.SaveChangesAsync();

        (await GetOutboxRowsAsync()).Should().BeEmpty(
            "with no processor running, a row is a write nobody reads");
        _mockDispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        entity.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveChangesAsync_IntegrationEvent_StillReachesItsHandlersInProcess()
    {
        // The routing that keeps integration events OUT of in-process dispatch exists so the outbox
        // can carry them to the broker. With no outbox there is no carrier, so withholding them
        // would drop them outright: an in-process host must dispatch them like any other event.
        var entity = new DomainEventSaveChangesInterceptorOutboxRoutingTests.TestAggregate { Id = 1, Name = "Test" };
        var integrationEvent = new DomainEventSaveChangesInterceptorOutboxRoutingTests.TestIntegrationEvent();
        entity.AddDomainEvent(integrationEvent);
        _dbContext.TestAggregates.Add(entity);

        IDomainEvent[]? dispatched = null;
        _mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IDomainEvent>, CancellationToken>((events, _) => dispatched = [.. events])
            .Returns(Task.CompletedTask);

        await _dbContext.SaveChangesAsync();

        (await GetOutboxRowsAsync()).Should().BeEmpty();
        dispatched.Should().NotBeNull();
        dispatched.Should().ContainSingle().Which.Should().BeSameAs(integrationEvent);
    }

    [Fact]
    public async Task SaveChangesAsync_AThrowingHandler_DoesNotStopTheNextEvent_AndLogsThatNothingRetries()
    {
        // M255: with no outbox row behind the dispatch, one batched call lost every event after the
        // first throwing handler while the log claimed the outbox would retry. Each event now
        // dispatches on its own and the failure is logged at Error as not retried.
        var entity = new DomainEventSaveChangesInterceptorOutboxRoutingTests.TestAggregate { Id = 1, Name = "Test" };
        var first = new DomainEventSaveChangesInterceptorOutboxRoutingTests.TestLocalEvent("A");
        var second = new DomainEventSaveChangesInterceptorOutboxRoutingTests.TestLocalEvent("B");
        entity.AddDomainEvent(first);
        entity.AddDomainEvent(second);
        _dbContext.TestAggregates.Add(entity);

        _mockDispatcher
            .Setup(d => d.DispatchAsync(
                It.Is<IEnumerable<IDomainEvent>>(events => events.Contains(first)),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("handler failed"));

        await _dbContext.SaveChangesAsync();

        _mockDispatcher.Verify(
            d => d.DispatchAsync(
                It.Is<IEnumerable<IDomainEvent>>(events => events.Contains(second)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        entity.DomainEvents.Should().BeEmpty();
        _logger.Entries.Where(e => e.Level == LogLevel.Error).Should().ContainSingle()
            .Which.Message.Should().Contain("not retried");
        _logger.Entries.Should().NotContain(e => e.Message.Contains("will retry", StringComparison.Ordinal));
    }

    [Fact]
    public void SaveChanges_Sync_LeavesTheEventsPendingForALaterAsyncSave()
    {
        // The sync path cannot await the dispatcher. With the outbox on, clearing the events is safe
        // because their rows deliver them; with it off, clearing would be the one place an event
        // could vanish, so the legacy no-op is kept and the next async save delivers them.
        var entity = new DomainEventSaveChangesInterceptorOutboxRoutingTests.TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new DomainEventSaveChangesInterceptorOutboxRoutingTests.TestLocalEvent("sync"));
        _dbContext.TestAggregates.Add(entity);

        _dbContext.SaveChanges();

        entity.DomainEvents.Should().ContainSingle(
            "nothing persisted the event, so it must stay on the aggregate until something dispatches it");
        _dbContext.Set<OutboxMessage>().AsNoTracking().ToList().Should().BeEmpty();
    }

    /// <summary>
    /// Hand-rolled capture of the interceptor's log lines; the interceptor is sealed and its
    /// messages are source-generated, so the rendered text is what the assertions read.
    /// </summary>
    private sealed class RecordingLogger : ILogger<DomainEventSaveChangesInterceptor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
