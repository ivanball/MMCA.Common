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
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
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
    private readonly OutboxProcessor _sut;

    public OutboxProcessorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dispatcherMock = new Mock<IDomainEventDispatcher>();

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
            .Setup(f => f.GetDbContext(DataSource.SQLServer))
            .Returns(_dbContext);

        var services = new ServiceCollection();
        services.AddSingleton(mockDbContextFactory.Object);
        services.AddSingleton(_dispatcherMock.Object);
        ServiceProvider rootProvider = services.BuildServiceProvider();

        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();
        var processorOutboxSignal = new Mock<MMCA.Common.Infrastructure.Persistence.Outbox.IOutboxSignal>();
        _sut = new OutboxProcessor(scopeFactory, NullLogger<OutboxProcessor>.Instance, Options.Create(new OutboxSettings()), processorOutboxSignal.Object);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _dbContext.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──

    /// <summary>
    /// Invokes the private <c>ProcessPendingMessagesAsync</c> method via reflection to avoid
    /// the 5-second startup delay and infinite loop of <c>ExecuteAsync</c>.
    /// </summary>
    private async Task InvokeProcessPendingMessagesAsync()
    {
        MethodInfo? method = typeof(OutboxProcessor)
            .GetMethod("ProcessPendingMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        method.Should().NotBeNull("OutboxProcessor must have a private ProcessPendingMessagesAsync method");

        var task = (Task)method!.Invoke(_sut, [CancellationToken.None])!;
        await task.ConfigureAwait(false);
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
        // Arrange: OccurredOn is now, within the 30-second processing delay window
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

    // ── Test doubles ──

    /// <summary>
    /// A minimal domain event used by tests to provide a resolvable type for deserialization.
    /// </summary>
    public sealed class TestDomainEvent : IDomainEvent
    {
        public DateTime DateOccurred { get; init; }
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
        : ApplicationDbContext(options, serviceProvider, assemblyProvider)
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
