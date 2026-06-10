using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

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
        var outboxSignal = new Mock<MMCA.Common.Infrastructure.Persistence.Outbox.IOutboxSignal>();
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
    /// individual tests vary <see cref="OutboxSettings.BatchSize"/> etc.
    /// </summary>
    private OutboxProcessor CreateProcessor(OutboxSettings settings) =>
        new(
            _scopeFactory,
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(settings),
            Mock.Of<MMCA.Common.Infrastructure.Persistence.Outbox.IOutboxSignal>(),
            _registry,
            _resolver);

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
    public async Task DeadLettersUnresolvableTypes_SetsProcessedOnAndLastError()
    {
        // Arrange: EventType references a type that does not exist
        OutboxMessage message = CreateEligibleMessage(eventType: "NonExistent.Type, FakeAssembly");
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
