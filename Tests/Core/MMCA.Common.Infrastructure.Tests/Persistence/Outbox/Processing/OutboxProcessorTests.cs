using System.Diagnostics.Metrics;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Administration;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.Resilience;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Outbox.Processing;

/// <summary>
/// Unit tests for <see cref="OutboxProcessor"/> covering batch processing, dead-lettering,
/// retry logic, and message filtering.
/// </summary>
public sealed class OutboxProcessorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OutboxTestDbContext _dbContext;
    private readonly Mock<IDomainEventDispatcher> _dispatcherMock;
    private readonly Mock<IMessageBus> _messageBusMock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEntityDataSourceRegistry _registry;
    private readonly IDataSourceResolver _resolver;
    private readonly OutboxProcessor _sut;

    public OutboxProcessorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dispatcherMock = new Mock<IDomainEventDispatcher>();
        _messageBusMock = new Mock<IMessageBus>();

        // Build a service provider that includes interceptor dependencies so the
        // test DbContext can resolve them via its base OnConfiguring.
        var contextServices = new ServiceCollection();
        contextServices.AddSingleton(TimeProvider.System);
        contextServices.AddSingleton(_dispatcherMock.Object);
        contextServices.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
        var outboxSignal = new Mock<MMCA.Common.Infrastructure.Persistence.Outbox.Processing.IOutboxSignal>();
        contextServices.AddSingleton(new DomainEventSaveChangesInterceptor(
            _dispatcherMock.Object, NullLogger<DomainEventSaveChangesInterceptor>.Instance, outboxSignal.Object));
        contextServices.AddSingleton(Mock.Of<IEntityConfigurationAssemblyProvider>(
            p => p.GetConfigurationAssemblies() == Array.Empty<Assembly>()));
        contextServices.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
        ServiceProvider contextSp = contextServices.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<OutboxTestDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new OutboxTestDbContext(
            options,
            contextSp,
            Mock.Of<IEntityConfigurationAssemblyProvider>(
                p => p.GetConfigurationAssemblies() == Array.Empty<Assembly>()));

        _dbContext.Database.EnsureCreated();

        var mockDbContextFactory = new Mock<IDbContextFactory>();
        mockDbContextFactory
            .Setup(f => f.GetDbContext(DataSourceKey.Default(DataSource.SQLServer)))
            .Returns(_dbContext);

        var services = new ServiceCollection();
        services.AddSingleton(mockDbContextFactory.Object);
        services.AddSingleton(_dispatcherMock.Object);
        services.AddSingleton(_messageBusMock.Object);
        ServiceProvider rootProvider = services.BuildServiceProvider();

        _scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        var registryMock = new Mock<IEntityDataSourceRegistry>();
        registryMock.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);
        _registry = registryMock.Object;

        var resolverMock = new Mock<IDataSourceResolver>();
        resolverMock
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));
        _resolver = resolverMock.Object;

        _sut = CreateProcessor(new OutboxSettings());
    }

    /// <summary>
    /// Builds a processor over the shared SQLite fixture with the given settings — lets
    /// individual tests vary <see cref="OutboxSettings.BatchSize"/>, drive the clock via a
    /// <see cref="FakeTimeProvider"/>, or observe logging.
    /// </summary>
    private OutboxProcessor CreateProcessor(
        OutboxSettings settings,
        TimeProvider? timeProvider = null,
        ILogger<OutboxProcessor>? logger = null) =>
        new(
            _scopeFactory,
            logger ?? NullLogger<OutboxProcessor>.Instance,
            Options.Create(settings),
            Mock.Of<MMCA.Common.Infrastructure.Persistence.Outbox.Processing.IOutboxSignal>(),
            _registry,
            _resolver,
            timeProvider);

    public void Dispose()
    {
        _sut.Dispose();
        _dbContext.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──

    /// <summary>
    /// Invokes the internal <c>ProcessPendingMessagesAsync</c> method directly to avoid the
    /// 5-second startup delay and infinite loop of <c>ExecuteAsync</c>, returning the cycle
    /// result so tests can assert on the smart-wait inputs.
    /// </summary>
    private Task<OutboxCycleResult> InvokeProcessPendingMessagesAsync() =>
        _sut.ProcessPendingMessagesAsync(CancellationToken.None);

    /// <summary>
    /// Starts a <see cref="MeterListener"/> scoped to one instrument on the outbox meter and
    /// collects every measurement plus its <c>event_type</c> tag into <paramref name="sink"/>.
    /// A raw listener rather than <c>MetricCollector&lt;T&gt;</c>: the test project does not
    /// reference Microsoft.Extensions.Diagnostics.Testing, and package versions are centrally
    /// managed, so the listener is the dependency-free option (it is also what the dead-letter
    /// test above already uses).
    /// </summary>
    private static MeterListener StartOutboxListener<T>(
        string instrumentName,
        System.Threading.Lock gate,
        List<(T Value, string? EventType)> sink)
        where T : struct
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, "MMCA.Common.Outbox", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<T>((_, value, tags, _) =>
        {
            string? eventType = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (string.Equals(tag.Key, "event_type", StringComparison.Ordinal))
                {
                    eventType = tag.Value as string;
                }
            }

            lock (gate)
            {
                sink.Add((value, eventType));
            }
        });

        listener.Start();
        return listener;
    }

    /// <summary>
    /// Creates an outbox message eligible for processing (old enough, unprocessed, zero retries).
    /// </summary>
    private static OutboxMessage CreateEligibleMessage(
        string? eventType = null,
        string? payload = null,
        DateTime? occurredOn = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            EventType = eventType ?? typeof(TestDomainEvent).AssemblyQualifiedName!,
            Payload = payload ?? """{"DateOccurred":"2025-01-01T00:00:00Z"}""",
            OccurredOn = occurredOn ?? DateTime.UtcNow.AddMinutes(-5),
            ProcessedOn = null,
            RetryCount = 0,
        };

    [Fact]
    public async Task ProcessesBatchSuccessfully_SetsProcessedOnForAllMessages()
    {
        // Arrange
        var messages = Enumerable.Range(0, 3).Select(_ => CreateEligibleMessage()).ToArray();
        await _dbContext.Set<OutboxMessage>().AddRangeAsync(messages);
        await _dbContext.SaveChangesAsync();

        // Act
        await InvokeProcessPendingMessagesAsync();

        // Assert
        List<OutboxMessage> updated = await _dbContext.Set<OutboxMessage>().ToListAsync();
        updated.Should().HaveCount(3);
        updated.Should().AllSatisfy(m =>
        {
            m.ProcessedOn.Should().NotBeNull();
            m.LastError.Should().BeNull();
            m.RetryCount.Should().Be(0);
        });
    }

    [Fact]
    public async Task SkipsRecentMessages_DoesNotProcessMessagesWithinDelay()
    {
        // Arrange: OccurredOn is now, within the 5-second processing delay window
        OutboxMessage message = CreateEligibleMessage(occurredOn: DateTime.UtcNow);
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        // Act
        await InvokeProcessPendingMessagesAsync();

        // Assert
        OutboxMessage unchanged = await _dbContext.Set<OutboxMessage>().SingleAsync();
        unchanged.ProcessedOn.Should().BeNull();
        unchanged.RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task UnresolvableType_FirstAttempt_RetriesInsteadOfDeadLettering()
    {
        // A name that does not resolve right now is not proof it never will: the assembly declaring
        // it may load a moment later. The first attempt therefore goes through the normal retry path.
        OutboxMessage message = CreateEligibleMessage(eventType: "NonExistent.Type, FakeAssembly");
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        await InvokeProcessPendingMessagesAsync();

        OutboxMessage retried = await _dbContext.Set<OutboxMessage>().SingleAsync();
        retried.ProcessedOn.Should().BeNull("the first unresolved attempt is treated as transient");
        retried.RetryCount.Should().Be(1);
        retried.LastError.Should().Contain("Cannot resolve type");
        retried.LockedUntil.Should().NotBeNull("the row is re-leased for its backoff like any other retry");
    }

    [Fact]
    public async Task DeadLettersUnresolvableTypes_SetsProcessedOnAndLastError()
    {
        // Arrange: EventType references a type that does not exist, and the row has already spent
        // its one transient attempt, so THIS cycle is the terminal one.
        OutboxMessage message = CreateEligibleMessage(eventType: "NonExistent.Type, FakeAssembly");
        message.RetryCount = 1;
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        // Act
        await InvokeProcessPendingMessagesAsync();

        // Assert
        OutboxMessage deadLettered = await _dbContext.Set<OutboxMessage>().SingleAsync();
        deadLettered.ProcessedOn.Should().NotBeNull("dead-lettered messages are marked as processed");
        deadLettered.LastError.Should().Contain("Cannot resolve type");
    }

    [Fact]
    public async Task OldestPendingAgeGauge_ReportsTheAgeOfTheOldestPendingRow_AndZeroWhenDrained()
    {
        // The gauge is computed from the poll's own ordered fetch, so it must move with the backlog
        // without the processor issuing a MIN() of its own.
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
        OutboxMessage message = CreateEligibleMessage(
            occurredOn: timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-30));
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        // The gauge is process-wide static state keyed by source name, so this test drives its own
        // uniquely named source: a sibling test class polling the shared default source in parallel
        // would otherwise overwrite the value between the act and the assert.
        var probeSource = new DataSourceKey(DataSource.SQLServer, "GaugeProbe_" + Guid.NewGuid().ToString("N"));
        OutboxProcessor sut = CreateProcessorForSource(probeSource, new OutboxSettings(), timeProvider);

        using var listener = StartOldestPendingAgeListener(probeSource.ToString(), out var readLatestAge);

        await sut.ProcessPendingMessagesAsync(CancellationToken.None);

        readLatestAge().Should().BeApproximately(
            TimeSpan.FromMinutes(30).TotalSeconds,
            precision: 1,
            "the oldest pending row was raised half an hour before this cycle's clock");

        // Second cycle: the row was dispatched above, so nothing is pending any more.
        await sut.ProcessPendingMessagesAsync(CancellationToken.None);
        readLatestAge().Should().Be(0, "a drained source reports zero rather than dropping out of the series");
    }

    /// <summary>
    /// Builds a processor whose single outbox source is <paramref name="source"/>, backed by this
    /// fixture's context, so a test can own a source name no other test publishes metrics for.
    /// </summary>
    private OutboxProcessor CreateProcessorForSource(
        DataSourceKey source,
        OutboxSettings settings,
        TimeProvider timeProvider)
    {
        var contextFactory = new Mock<IDbContextFactory>();
        contextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(_dbContext);

        var services = new ServiceCollection();
        services.AddSingleton(contextFactory.Object);
        services.AddSingleton(_dispatcherMock.Object);
        services.AddSingleton(_messageBusMock.Object);
        ServiceProvider provider = services.BuildServiceProvider();

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([source]);

        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns(source);

        return new OutboxProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(settings),
            Mock.Of<MMCA.Common.Infrastructure.Persistence.Outbox.Processing.IOutboxSignal>(),
            registry.Object,
            resolver.Object,
            timeProvider);
    }

    /// <summary>
    /// Listens for <c>outbox.oldest_pending.age</c> measurements tagged with
    /// <paramref name="dataSourceName"/>. An observable gauge only produces a value when the
    /// listener asks, so <paramref name="readLatestAge"/> forces a collection on each call.
    /// </summary>
    private static MeterListener StartOldestPendingAgeListener(
        string dataSourceName,
        out Func<double> readLatestAge)
    {
        var box = new double[] { -1 };
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, "MMCA.Common.Outbox", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "outbox.oldest_pending.age", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (string.Equals(tag.Key, "data_source", StringComparison.Ordinal)
                    && string.Equals(tag.Value as string, dataSourceName, StringComparison.Ordinal))
                {
                    box[0] = value;
                }
            }
        });

        listener.Start();

        var captured = listener;
        readLatestAge = () =>
        {
            box[0] = -1;
            captured.RecordObservableInstruments();
            return box[0];
        };

        return listener;
    }

    [Fact]
    public async Task IncrementsRetryOnDispatchFailure_SetsRetryCountAndLastError()
    {
        // Arrange
        _dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Dispatch failed"));

        OutboxMessage message = CreateEligibleMessage();
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        // Act
        await InvokeProcessPendingMessagesAsync();

        // Assert
        OutboxMessage retried = await _dbContext.Set<OutboxMessage>().SingleAsync();
        retried.ProcessedOn.Should().BeNull("failed messages should not be marked as processed");
        retried.RetryCount.Should().Be(1);
        retried.LastError.Should().Be("Dispatch failed");
    }

    [Fact]
    public async Task SkipsAlreadyProcessedMessages_DoesNotReprocess()
    {
        // Arrange: message already has ProcessedOn set
        var processedTime = DateTime.UtcNow.AddMinutes(-10);
        OutboxMessage message = CreateEligibleMessage();
        message.ProcessedOn = processedTime;
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        // Act
        await InvokeProcessPendingMessagesAsync();

        // Assert: ProcessedOn remains unchanged (not updated to a newer time)
        OutboxMessage unchanged = await _dbContext.Set<OutboxMessage>().SingleAsync();
        unchanged.ProcessedOn.Should().Be(processedTime);
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SkipsMaxRetriedMessages_DoesNotPickUpExhaustedRetries()
    {
        // Arrange: message has already exhausted all 5 retries
        OutboxMessage message = CreateEligibleMessage();
        message.RetryCount = 5;
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        // Act
        await InvokeProcessPendingMessagesAsync();

        // Assert
        OutboxMessage unchanged = await _dbContext.Set<OutboxMessage>().SingleAsync();
        unchanged.ProcessedOn.Should().BeNull();
        unchanged.RetryCount.Should().Be(5);
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ContinuesProcessingAfterIndividualFailure_ProcessesRemainingMessages()
    {
        // Arrange: two eligible messages; dispatcher fails on the first, succeeds on the second.
        OutboxMessage failMessage = CreateEligibleMessage(occurredOn: DateTime.UtcNow.AddMinutes(-10));
        OutboxMessage successMessage = CreateEligibleMessage(occurredOn: DateTime.UtcNow.AddMinutes(-5));

        await _dbContext.Set<OutboxMessage>().AddRangeAsync(failMessage, successMessage);
        await _dbContext.SaveChangesAsync();

        int callCount = 0;
        _dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IDomainEvent>, CancellationToken>((_, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new InvalidOperationException("First message fails");
                }

                return Task.CompletedTask;
            });

        // Act
        await InvokeProcessPendingMessagesAsync();

        // Assert
        OutboxMessage failed = await _dbContext.Set<OutboxMessage>().SingleAsync(m => m.Id == failMessage.Id);
        failed.RetryCount.Should().Be(1);
        failed.LastError.Should().Be("First message fails");
        failed.ProcessedOn.Should().BeNull();

        OutboxMessage succeeded = await _dbContext.Set<OutboxMessage>().SingleAsync(m => m.Id == successMessage.Id);
        succeeded.ProcessedOn.Should().NotBeNull();
        succeeded.RetryCount.Should().Be(0);
        succeeded.LastError.Should().BeNull();
    }

    [Fact]
    public async Task PendingOnlyMessages_ReturnsEarliestPending_AndProcessesNothing()
    {
        // Arrange: both messages are younger than the 5s processing delay — not yet eligible.
        OutboxMessage older = CreateEligibleMessage(occurredOn: DateTime.UtcNow.AddSeconds(-2));
        OutboxMessage newer = CreateEligibleMessage(occurredOn: DateTime.UtcNow.AddSeconds(-1));
        await _dbContext.Set<OutboxMessage>().AddRangeAsync(older, newer);
        await _dbContext.SaveChangesAsync();

        // Act
        OutboxCycleResult result = await InvokeProcessPendingMessagesAsync();

        // Assert: nothing processed, the oldest pending timestamp drives the smart wait.
        result.HasMoreEligibleWork.Should().BeFalse();
        result.EarliestPendingOccurredOn.Should().NotBeNull();
        result.EarliestPendingOccurredOn!.Value.Should().BeCloseTo(older.OccurredOn, TimeSpan.FromMilliseconds(1));

        List<OutboxMessage> all = await _dbContext.Set<OutboxMessage>().ToListAsync();
        all.Should().AllSatisfy(m => m.ProcessedOn.Should().BeNull());
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MixedEligibleAndPending_ProcessesOnlyEligible_ReturnsPendingTimestamp()
    {
        // Arrange: one eligible message (5 min old) and one pending (just written).
        OutboxMessage eligible = CreateEligibleMessage();
        OutboxMessage pending = CreateEligibleMessage(occurredOn: DateTime.UtcNow);
        await _dbContext.Set<OutboxMessage>().AddRangeAsync(eligible, pending);
        await _dbContext.SaveChangesAsync();

        // Act
        OutboxCycleResult result = await InvokeProcessPendingMessagesAsync();

        // Assert
        result.HasMoreEligibleWork.Should().BeFalse("the eligible batch was not full");
        result.EarliestPendingOccurredOn.Should().NotBeNull();
        result.EarliestPendingOccurredOn!.Value.Should().BeCloseTo(pending.OccurredOn, TimeSpan.FromMilliseconds(1));

        OutboxMessage processed = await _dbContext.Set<OutboxMessage>().SingleAsync(m => m.Id == eligible.Id);
        processed.ProcessedOn.Should().NotBeNull();
        OutboxMessage untouched = await _dbContext.Set<OutboxMessage>().SingleAsync(m => m.Id == pending.Id);
        untouched.ProcessedOn.Should().BeNull();
    }

    [Fact]
    public async Task FullEligibleBatch_WithProgress_ReportsMoreEligibleWork()
    {
        // Arrange: three eligible messages but a batch size of two — the first cycle drains a
        // full batch successfully, so the processor should re-poll immediately.
        using OutboxProcessor processor = CreateProcessor(new OutboxSettings { BatchSize = 2 });
        var messages = Enumerable.Range(0, 3).Select(_ => CreateEligibleMessage()).ToArray();
        await _dbContext.Set<OutboxMessage>().AddRangeAsync(messages);
        await _dbContext.SaveChangesAsync();

        // Act
        OutboxCycleResult result = await processor.ProcessPendingMessagesAsync(CancellationToken.None);

        // Assert
        result.HasMoreEligibleWork.Should().BeTrue("a full batch made progress, more rows may be waiting");
    }

    [Fact]
    public async Task FullEligibleBatch_AllFailing_DoesNotReportMoreEligibleWork()
    {
        // Arrange: a full batch where every dispatch fails — without the progress guard the
        // processor would hot-spin retrying the same rows back-to-back.
        _dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Dispatch failed"));

        using OutboxProcessor processor = CreateProcessor(new OutboxSettings { BatchSize = 2 });
        var messages = Enumerable.Range(0, 2).Select(_ => CreateEligibleMessage()).ToArray();
        await _dbContext.Set<OutboxMessage>().AddRangeAsync(messages);
        await _dbContext.SaveChangesAsync();

        // Act
        OutboxCycleResult result = await processor.ProcessPendingMessagesAsync(CancellationToken.None);

        // Assert: no progress → no immediate re-poll, and failed-but-eligible rows must not
        // shorten the smart wait (they retry on the next signal or polling interval).
        result.HasMoreEligibleWork.Should().BeFalse("a fully-failing batch must not hot-spin the processor");
        result.EarliestPendingOccurredOn.Should().BeNull("failed eligible rows are not pending rows");
    }

    [Fact]
    public async Task IntegrationEvent_RoutedThroughMessageBus_NotDispatcher()
    {
        // Arrange: an outbox entry whose CLR type implements IIntegrationEvent.
        OutboxMessage message = CreateEligibleMessage(
            eventType: typeof(TestIntegrationEvent).AssemblyQualifiedName);
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        // Act
        await InvokeProcessPendingMessagesAsync();

        // Assert: published via IMessageBus, NOT IDomainEventDispatcher.
        OutboxMessage processed = await _dbContext.Set<OutboxMessage>().SingleAsync();
        processed.ProcessedOn.Should().NotBeNull();
        processed.LastError.Should().BeNull();
        processed.RetryCount.Should().Be(0);

        _messageBusMock.Verify(
            b => b.PublishAsync(It.IsAny<IIntegrationEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IntegrationEventPublishFailure_DegradesGracefully_BuffersForRedelivery()
    {
        // Chaos / fault injection (C-8): the broker (IMessageBus) is unreachable. The outbox must
        // degrade gracefully — increment the retry count, record the error, and LEAVE the message
        // unprocessed so a later poll redelivers it (ADR-009 graceful degradation) — never crash.
        _messageBusMock
            .Setup(b => b.PublishAsync(It.IsAny<IIntegrationEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker unreachable"));

        OutboxMessage message = CreateEligibleMessage(
            eventType: typeof(TestIntegrationEvent).AssemblyQualifiedName);
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        // Act — must not throw even though the dependency is down.
        var act = async () => await InvokeProcessPendingMessagesAsync();
        await act.Should().NotThrowAsync();

        // Assert — buffered for retry, not dead-lettered and not marked delivered.
        OutboxMessage retried = await _dbContext.Set<OutboxMessage>().SingleAsync();
        retried.ProcessedOn.Should().BeNull("a broker failure must not mark the event delivered");
        retried.RetryCount.Should().Be(1);
        retried.LastError.Should().Be("Broker unreachable");
    }

    [Fact]
    public async Task SustainedPublishFailures_OpenTheBrokerCircuit_RowsStillRetryAndTheOpeningIsReportedOnce()
    {
        // A dead broker makes every publish wait out its own transport timeout, so one batch can
        // spend minutes discovering the same fact 50 times. The breaker turns the later attempts
        // into an immediate rejection; nothing is lost, because a rejected row follows the normal
        // failure path (retry increment, re-lease) and is retried on a later cycle.
        _messageBusMock
            .Setup(b => b.PublishAsync(It.IsAny<IIntegrationEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker unreachable"));

        // The breaker only evaluates its failure ratio once it has MinimumThroughput samples in the
        // window, so the batch has to be larger than that threshold to open mid-cycle.
        var messageCount = BrokerResilienceDefaults.MinimumThroughput + 5;
        OutboxMessage[] messages = [.. Enumerable.Range(0, messageCount).Select(i =>
            CreateEligibleMessage(
                eventType: typeof(TestIntegrationEvent).AssemblyQualifiedName,
                occurredOn: DateTime.UtcNow.AddMinutes(-10).AddSeconds(i)))];
        await _dbContext.Set<OutboxMessage>().AddRangeAsync(messages);
        await _dbContext.SaveChangesAsync();

        var logged = new List<(LogLevel Level, string Message)>();
        var mockLogger = new Mock<ILogger<OutboxProcessor>>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        mockLogger
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var formatter = (Delegate)invocation.Arguments[4];
                logged.Add((
                    (LogLevel)invocation.Arguments[0],
                    (string)formatter.DynamicInvoke(invocation.Arguments[2], invocation.Arguments[3])!));
            }));

        var gate = new System.Threading.Lock();
        var circuitOpenMeasurements = new List<(long Value, string? EventType)>();
        using MeterListener listener = StartBrokerListener(gate, circuitOpenMeasurements);

        using OutboxProcessor processor = CreateProcessor(new OutboxSettings(), logger: mockLogger.Object);
        await processor.ProcessPendingMessagesAsync(CancellationToken.None);

        // Every row took the normal failure path: nothing is marked delivered, nothing is lost.
        List<OutboxMessage> updated = await _dbContext.Set<OutboxMessage>().ToListAsync();
        updated.Should().HaveCount(messageCount);
        updated.Should().AllSatisfy(m =>
        {
            m.ProcessedOn.Should().BeNull("a broker failure must not mark the event delivered");
            m.RetryCount.Should().Be(1);
        });

        // Some rows never reached the broker at all: that is the breaker doing its job.
        circuitOpenMeasurements.Should().NotBeEmpty("the circuit must open once the failure ratio is provable");
        updated.Should().Contain(
            m => m.LastError != null && m.LastError.Contains("circuit", StringComparison.OrdinalIgnoreCase),
            "a short-circuited publish records the rejection, not a transport error it never saw");

        // Once per batch, not once per row: an open circuit rejects the whole remainder in the
        // same instant, and 50 identical warnings is noise an operator learns to filter.
        logged.Where(e => e.Message.Contains("circuit is open", StringComparison.Ordinal))
            .Should().ContainSingle();
    }

    /// <summary>
    /// Listener for the broker meter's circuit-open counter, mirroring
    /// <see cref="StartOutboxListener{T}"/> for the outbox meter.
    /// </summary>
    private static MeterListener StartBrokerListener(
        System.Threading.Lock gate,
        List<(long Value, string? EventType)> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, "MMCA.Common.Broker", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "broker.circuit.open.count", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? eventType = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (string.Equals(tag.Key, "event_type", StringComparison.Ordinal))
                {
                    eventType = tag.Value as string;
                }
            }

            lock (gate)
            {
                sink.Add((value, eventType));
            }
        });
        listener.Start();
        return listener;
    }

    // ── Lease: rows under an unexpired lock are skipped; expired locks are claimable ──
    [Fact]
    public async Task LockedRow_SkippedWhileLeaseUnexpired_ClaimedAndProcessedAfterExpiry()
    {
        // Arrange: a row "claimed" by another replica whose lease has 60s left.
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        using OutboxProcessor processor = CreateProcessor(new OutboxSettings(), timeProvider);

        var otherReplicaToken = Guid.NewGuid();
        OutboxMessage locked = CreateEligibleMessage(occurredOn: now.AddMinutes(-10));
        locked.LockedUntil = now.AddSeconds(60);
        locked.LockToken = otherReplicaToken;
        _dbContext.Set<OutboxMessage>().Add(locked);
        await _dbContext.SaveChangesAsync();

        // Act 1: the unexpired lease hides the row from this replica entirely.
        await processor.ProcessPendingMessagesAsync(CancellationToken.None);

        OutboxMessage stillLocked = await _dbContext.Set<OutboxMessage>().AsNoTracking().SingleAsync();
        stillLocked.ProcessedOn.Should().BeNull("a row under another replica's unexpired lease must be skipped");
        stillLocked.LockToken.Should().Be(otherReplicaToken, "a skipped row's claim must not be overwritten");
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Act 2: once the lease expires, the row is claimable again (dead-replica recovery).
        timeProvider.Advance(TimeSpan.FromSeconds(120));
        await processor.ProcessPendingMessagesAsync(CancellationToken.None);

        // Assert: claimed under a fresh token and dispatched.
        OutboxMessage processed = await _dbContext.Set<OutboxMessage>().AsNoTracking().SingleAsync();
        processed.ProcessedOn.Should().NotBeNull("an expired lease releases the row to the next replica");
        processed.LockToken.Should().NotBe(otherReplicaToken, "the claim step must stamp the claiming replica's own token");
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Retry exhaustion: Error log + dead-letter metric with reason=retries_exhausted ──
    [Fact]
    public async Task RetryExhaustion_LogsErrorAndIncrementsDeadLetterMetric()
    {
        // Arrange: MaxRetries 1, so the first failure exhausts the message.
        var mockLogger = new Mock<ILogger<OutboxProcessor>>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        using OutboxProcessor processor = CreateProcessor(
            new OutboxSettings { MaxRetries = 1 }, logger: mockLogger.Object);

        _dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("permanently failing handler"));

        var gate = new System.Threading.Lock();
        var measurements = new List<(long Value, string? Reason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "MMCA.Common.Outbox" && instrument.Name == "outbox.dead_letter.count")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? reason = null;
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, "reason", StringComparison.Ordinal))
                {
                    reason = tag.Value as string;
                }
            }

            lock (gate)
            {
                measurements.Add((value, reason));
            }
        });
        listener.Start();

        OutboxMessage message = CreateEligibleMessage();
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        // Act
        await processor.ProcessPendingMessagesAsync(CancellationToken.None);

        // Assert: the row leaves the poll via the RetryCount filter, never marked processed.
        OutboxMessage exhausted = await _dbContext.Set<OutboxMessage>().SingleAsync();
        exhausted.RetryCount.Should().Be(1);
        exhausted.ProcessedOn.Should().BeNull("an undelivered message must never be marked processed");
        exhausted.LastError.Should().Be("permanently failing handler");

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.Is<EventId>(e => e.Name == "LogRetriesExhausted"),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "exhaustion is the operator's last loud signal and must log at Error");

        lock (gate)
        {
            measurements.Should().Contain(
                m => m.Value == 1 && m.Reason == "retries_exhausted",
                "the dead-letter metric must record the exhaustion with its reason tag");
        }
    }

    // ── Shutdown mid-batch: stamps collected before the cancellation are flushed on the way out ──
    [Fact]
    public async Task CancellationMidBatch_PersistsStampsOfAlreadyDispatchedMessages()
    {
        // Arrange: two eligible messages, oldest first. The dispatcher delivers the first, then
        // host shutdown cancels the token during the second.
        using var cts = new CancellationTokenSource();
        OutboxMessage delivered = CreateEligibleMessage(occurredOn: DateTime.UtcNow.AddMinutes(-10));
        OutboxMessage interrupted = CreateEligibleMessage(occurredOn: DateTime.UtcNow.AddMinutes(-5));
        await _dbContext.Set<OutboxMessage>().AddRangeAsync(delivered, interrupted);
        await _dbContext.SaveChangesAsync();

        var calls = 0;
        _dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IDomainEvent>, CancellationToken>(async (_, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    return;
                }

                await cts.CancelAsync();
                throw new OperationCanceledException(cts.Token);
            });

        // Act
        Func<Task> act = () => _sut.ProcessPendingMessagesAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>("shutdown must still reach the polling loop");

        // Assert: the delivered message is stamped IN THE DATABASE, not just in the change tracker,
        // so it is not redelivered when its lease expires.
        List<OutboxMessage> persisted = await _dbContext.Set<OutboxMessage>().AsNoTracking().ToListAsync();
        persisted.Single(m => m.Id == delivered.Id).ProcessedOn.Should().NotBeNull(
            "a message delivered before the cancellation must not be redelivered after the lease expires");
        persisted.Single(m => m.Id == interrupted.Id).ProcessedOn.Should().BeNull(
            "the message the cancellation interrupted was never delivered");
    }

    // ── Shutdown mid-batch: a failing best-effort save must not mask the cancellation ──
    [Fact]
    public async Task CancellationMidBatch_WhenTheShutdownSaveFails_StillPropagatesTheCancellation()
    {
        // Arrange: the dispatcher cancels AND the database is gone, so the best-effort save on the
        // way out fails too. The polling loop recognizes shutdown by the exception type, so
        // swapping the cancellation for the save failure would turn a clean stop into an error
        // plus another polling cycle.
        using var cts = new CancellationTokenSource();
        OutboxMessage message = CreateEligibleMessage();
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        _dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IDomainEvent>, CancellationToken>(async (_, _) =>
            {
                _dbContext.FailSaves = true;
                await cts.CancelAsync();
                throw new OperationCanceledException(cts.Token);
            });

        // Act / Assert
        Func<Task> act = () => _sut.ProcessPendingMessagesAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "a best-effort save failure must never replace the propagating cancellation");

        _dbContext.FailSaves = false;
    }

    // ── Metrics: processed count, dispatch lag, and observed backlog depth ──
    [Fact]
    public async Task DispatchedMessages_IncrementTheProcessedCounter_TaggedByEventType()
    {
        // Arrange
        var gate = new System.Threading.Lock();
        var measurements = new List<(long Value, string? EventType)>();
        using MeterListener listener = StartOutboxListener("outbox.processed.count", gate, measurements);

        var messages = Enumerable.Range(0, 3).Select(_ => CreateEligibleMessage()).ToArray();
        await _dbContext.Set<OutboxMessage>().AddRangeAsync(messages);
        await _dbContext.SaveChangesAsync();

        // Act
        await InvokeProcessPendingMessagesAsync();

        // Assert: one increment of 1 per dispatched message, carrying the event type tag.
        var expectedEventType = typeof(TestDomainEvent).AssemblyQualifiedName;
        lock (gate)
        {
            var forThisEventType = measurements
                .Where(m => string.Equals(m.EventType, expectedEventType, StringComparison.Ordinal))
                .ToList();

            forThisEventType.Should().HaveCountGreaterThanOrEqualTo(
                3,
                "the counter increments once per message stamped processed");
            forThisEventType.Should().AllSatisfy(m => m.Value.Should().Be(1));
        }
    }

    [Fact]
    public async Task DispatchedMessages_RecordTheDispatchLagInSeconds_TaggedByEventType()
    {
        // Arrange: a fake clock makes the lag exact. The row was written 10 minutes (600s) before
        // the instant the processor stamps it.
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        using OutboxProcessor processor = CreateProcessor(new OutboxSettings(), timeProvider);

        var gate = new System.Threading.Lock();
        var measurements = new List<(double Value, string? EventType)>();
        using MeterListener listener = StartOutboxListener("outbox.dispatch.lag", gate, measurements);

        OutboxMessage message = CreateEligibleMessage(occurredOn: now.AddMinutes(-10));
        _dbContext.Set<OutboxMessage>().Add(message);
        await _dbContext.SaveChangesAsync();

        // Act
        await processor.ProcessPendingMessagesAsync(CancellationToken.None);

        // Assert
        var expectedEventType = typeof(TestDomainEvent).AssemblyQualifiedName;
        lock (gate)
        {
            measurements.Should().AllSatisfy(
                m => m.Value.Should().BeGreaterThanOrEqualTo(0, "a delivery lag is never negative"));
            measurements.Should().Contain(
                m => m.Value > 599.9 && m.Value < 600.1
                    && string.Equals(m.EventType, expectedEventType, StringComparison.Ordinal),
                "the histogram records ProcessedOn minus OccurredOn in seconds");
        }
    }

    [Fact]
    public async Task PendingDepthGauge_ReportsTheBacklogObservedInTheLastCycle()
    {
        // Arrange: one eligible row plus two still inside the processing delay, so the cycle
        // polls a backlog of three.
        var gate = new System.Threading.Lock();
        var measurements = new List<(long Value, string? EventType)>();
        using MeterListener listener = StartOutboxListener("outbox.pending.depth", gate, measurements);

        OutboxMessage eligible = CreateEligibleMessage();
        OutboxMessage pendingA = CreateEligibleMessage(occurredOn: DateTime.UtcNow);
        OutboxMessage pendingB = CreateEligibleMessage(occurredOn: DateTime.UtcNow);
        await _dbContext.Set<OutboxMessage>().AddRangeAsync(eligible, pendingA, pendingB);
        await _dbContext.SaveChangesAsync();

        // Act
        await InvokeProcessPendingMessagesAsync();
        listener.RecordObservableInstruments();

        // Assert: the gauge is observed once and reports what this instance saw.
        lock (gate)
        {
            measurements.Should()
                .ContainSingle("an observable gauge yields one measurement per observation")
                .Which.Value.Should().Be(3);
        }
    }

    // ── Retry backoff: jittered so a batch that failed together does not retry in lockstep ──
    [Theory]
    [InlineData(1, 10d)]
    [InlineData(2, 20d)]
    [InlineData(3, 40d)]
    [InlineData(4, 80d)]
    public void RetryBackoff_StaysWithinTheJitterBandOfTheDeterministicBase(int retryCount, double deterministic)
    {
        // The backoff is no longer a single exact value: it is base * 2^(n-1) multiplied by a
        // random factor in [0.8, 1.2], so the contract is a band, not an equality.
        var settings = new OutboxSettings { RetryBackoffBaseSeconds = 10, LeaseSeconds = 3600 };
        using OutboxProcessor processor = CreateProcessor(settings);

        for (var i = 0; i < 50; i++)
        {
            var backoff = processor.ComputeRetryBackoffSeconds(retryCount);

            backoff.Should().BeInRange(
                deterministic * 0.8,
                deterministic * 1.2,
                "the jitter factor is bounded to plus or minus 20 percent of the deterministic backoff");
        }
    }

    [Fact]
    public void RetryBackoff_RemainsCappedAtTheLease_EvenAtTheTopOfTheJitterBand()
    {
        // Jitter is applied BEFORE the cap, so a backoff that overruns the lease still lands
        // exactly on it: a failing row never holds its claim longer than a dead replica's rows.
        var settings = new OutboxSettings { RetryBackoffBaseSeconds = 10, LeaseSeconds = 300 };
        using OutboxProcessor processor = CreateProcessor(settings);

        for (var i = 0; i < 50; i++)
        {
            var backoff = processor.ComputeRetryBackoffSeconds(10);

            backoff.Should().BeLessThanOrEqualTo(settings.LeaseSeconds);
            backoff.Should().BeApproximately(settings.LeaseSeconds, 1e-9);
        }
    }

    [Fact]
    public void RetryBackoff_VariesBetweenCalls_SoSimultaneousFailuresDoNotRetryTogether()
    {
        // The whole point of the jitter: 50 rows that failed in the same instant must not all
        // become claimable again at the same instant.
        var settings = new OutboxSettings { RetryBackoffBaseSeconds = 10, LeaseSeconds = 3600 };
        using OutboxProcessor processor = CreateProcessor(settings);

        var samples = Enumerable.Range(0, 50)
            .Select(_ => processor.ComputeRetryBackoffSeconds(3))
            .ToList();

        samples.Distinct().Should().HaveCountGreaterThan(
            1,
            "a deterministic backoff would make a whole failed batch retry in lockstep");
    }

    // ── Test doubles ──

    /// <summary>
    /// A minimal domain event used by tests to provide a resolvable type for deserialization.
    /// </summary>
    public sealed class TestDomainEvent : IDomainEvent
    {
        public DateTime DateOccurred { get; init; }

        public Guid MessageId { get; init; } = Guid.NewGuid();
    }

    /// <summary>
    /// A minimal integration event used to verify the OutboxProcessor routes
    /// <see cref="IIntegrationEvent"/> messages through <see cref="IMessageBus"/>
    /// rather than the in-process domain event dispatcher.
    /// </summary>
    public sealed class TestIntegrationEvent : IIntegrationEvent
    {
        public DateTime DateOccurred { get; init; }

        public Guid MessageId { get; init; } = Guid.NewGuid();
    }

    /// <summary>
    /// A test-specific <see cref="ApplicationDbContext"/> subclass that uses SQLite in-memory
    /// and configures only the <see cref="OutboxMessage"/> entity (without SQL Server–specific
    /// index filters that are incompatible with SQLite).
    /// </summary>
    private sealed class OutboxTestDbContext(
        DbContextOptions options,
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider)
        : ApplicationDbContext(options, serviceProvider, assemblyProvider, TestPhysicalDataSources.Sqlite())
    {
        internal override bool SupportsOutbox => true;

        /// <summary>When set, every save fails, standing in for a connection lost at shutdown.</summary>
        public bool FailSaves { get; set; }

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default) =>
            FailSaves
                ? Task.FromException<int>(new InvalidOperationException("connection lost"))
                : base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<OutboxMessage>(entity =>
            {
                entity.ToTable("OutboxMessages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.EventType).IsRequired().HasMaxLength(500);
                entity.Property(e => e.Payload).IsRequired();
                entity.Property(e => e.LastError).HasMaxLength(4000);
            });
    }
}
