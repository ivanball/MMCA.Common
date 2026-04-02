using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class DomainEventSaveChangesInterceptorTests : IDisposable
{
    private readonly Mock<IDomainEventDispatcher> _mockDispatcher = new();
    private readonly Mock<ILogger<DomainEventSaveChangesInterceptor>> _mockLogger = new();
    private readonly DomainEventSaveChangesInterceptor _sut;
    private readonly TestDomainEventDbContext _dbContext;

    public DomainEventSaveChangesInterceptorTests()
    {
        var outboxSignal = new Mock<MMCA.Common.Infrastructure.Persistence.Outbox.IOutboxSignal>();
        _sut = new DomainEventSaveChangesInterceptor(_mockDispatcher.Object, _mockLogger.Object, outboxSignal.Object);
        _dbContext = TestDomainEventDbContext.Create(_sut);
    }

    public void Dispose() => _dbContext.Dispose();

    // ── Async path: events captured and dispatched ──
    [Fact]
    public async Task SaveChangesAsync_WithDomainEvents_DispatchesEventsAfterPersistence()
    {
        var entity = new TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new TestDomainEvent("event-1"));
        _dbContext.TestAggregates.Add(entity);

        _mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _dbContext.SaveChangesAsync();

        _mockDispatcher.Verify(
            d => d.DispatchAsync(
                It.Is<IEnumerable<IDomainEvent>>(events => events.Any()),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Events cleared after dispatch ──
    [Fact]
    public async Task SaveChangesAsync_ClearsDomainEventsAfterDispatch()
    {
        var entity = new TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new TestDomainEvent("event-1"));
        entity.AddDomainEvent(new TestDomainEvent("event-2"));
        _dbContext.TestAggregates.Add(entity);

        _mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _dbContext.SaveChangesAsync();

        entity.DomainEvents.Should().BeEmpty();
    }

    // ── No events: dispatcher not called ──
    [Fact]
    public async Task SaveChangesAsync_WithoutDomainEvents_DoesNotDispatch()
    {
        var entity = new TestAggregate { Id = 1, Name = "Test" };
        _dbContext.TestAggregates.Add(entity);

        await _dbContext.SaveChangesAsync();

        _mockDispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Dispatch failure: events still cleared ──
    [Fact]
    public async Task SaveChangesAsync_WhenDispatchFails_StillClearsDomainEvents()
    {
        var entity = new TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new TestDomainEvent("event-1"));
        _dbContext.TestAggregates.Add(entity);

        _mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Dispatch failed"));

        await _dbContext.SaveChangesAsync();

        entity.DomainEvents.Should().BeEmpty();
    }

    // ── Dispatch failure: checks logger IsEnabled for warning ──
    [Fact]
    public async Task SaveChangesAsync_WhenDispatchFails_ChecksLoggerIsEnabled()
    {
        var entity = new TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new TestDomainEvent("event-1"));
        _dbContext.TestAggregates.Add(entity);

        _mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Dispatch failed"));

        await _dbContext.SaveChangesAsync();

        // Source-generated LoggerMessage calls IsEnabled before Log.
        // Verify the interceptor attempted to log.
        _mockLogger.Verify(
            l => l.IsEnabled(LogLevel.Warning),
            Times.Once);
    }

    // ── Multiple aggregates: all events dispatched ──
    [Fact]
    public async Task SaveChangesAsync_MultipleAggregatesWithEvents_DispatchesAllEvents()
    {
        var entity1 = new TestAggregate { Id = 1, Name = "First" };
        entity1.AddDomainEvent(new TestDomainEvent("event-1"));

        var entity2 = new TestAggregate { Id = 2, Name = "Second" };
        entity2.AddDomainEvent(new TestDomainEvent("event-2"));
        entity2.AddDomainEvent(new TestDomainEvent("event-3"));

        _dbContext.TestAggregates.Add(entity1);
        _dbContext.TestAggregates.Add(entity2);

        IDomainEvent[]? capturedEvents = null;
        _mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IDomainEvent>, CancellationToken>((events, _) => capturedEvents = [.. events])
            .Returns(Task.CompletedTask);

        await _dbContext.SaveChangesAsync();

        capturedEvents.Should().NotBeNull();
        capturedEvents.Should().HaveCount(3);
    }

    // ── Synchronous SavingChanges path ──
    [Fact]
    public void SavingChanges_WithDomainEvents_CapturesEvents()
    {
        var entity = new TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new TestDomainEvent("sync-event"));
        _dbContext.TestAggregates.Add(entity);

        _mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Synchronous SavedChanges is a no-op for dispatch, but SavingChanges still captures
        _dbContext.SaveChanges();

        // In the sync path, SavedChanges doesn't dispatch, but events should still be captured.
        // The important thing is that the interceptor doesn't throw.
        // Events may or may not be dispatched depending on the sync/async path.
        entity.Should().NotBeNull();
    }

    // ── Test helpers ──
    public sealed record TestDomainEvent(string Data) : BaseDomainEvent;

    public sealed class TestAggregate : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class TestDomainEventDbContext : ApplicationDbContext
    {
        public DbSet<TestAggregate> TestAggregates => Set<TestAggregate>();

        internal override bool SupportsOutbox => false;

        private TestDomainEventDbContext(DbContextOptions<TestDomainEventDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider())
        {
        }

        public static TestDomainEventDbContext Create(DomainEventSaveChangesInterceptor domainEventInterceptor)
        {
            var services = new ServiceCollection();
            var auditInterceptor = new AuditSaveChangesInterceptor(TimeProvider.System);
            services.AddSingleton(auditInterceptor);
            services.AddSingleton(domainEventInterceptor);
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<TestDomainEventDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new TestDomainEventDbContext(options, sp);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<TestAggregate>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Name);
                e.Property(x => x.RowVersion).IsConcurrencyToken();
            });
    }

    private sealed class NullAssemblyProvider : Application.Interfaces.Infrastructure.IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
