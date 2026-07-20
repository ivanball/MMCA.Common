using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Routing tests for <see cref="DomainEventSaveChangesInterceptor"/> on an outbox-enabled
/// context (<c>SupportsOutbox == true</c>): integration events go to the outbox only (rows
/// stay unprocessed for the <see cref="OutboxProcessor"/>), local events get the in-process
/// fast path (rows marked processed), and the sync save path clears events without
/// dispatching, leaving delivery entirely to the outbox.
/// </summary>
public sealed class DomainEventSaveChangesInterceptorOutboxRoutingTests : IDisposable
{
    private readonly Mock<IDomainEventDispatcher> _mockDispatcher = new();
    private readonly Mock<IOutboxSignal> _mockSignal = new();
    private readonly OutboxRoutingTestDbContext _dbContext;

    public DomainEventSaveChangesInterceptorOutboxRoutingTests()
    {
        var interceptor = new DomainEventSaveChangesInterceptor(
            _mockDispatcher.Object,
            NullLogger<DomainEventSaveChangesInterceptor>.Instance,
            _mockSignal.Object);
        _dbContext = OutboxRoutingTestDbContext.Create(interceptor);
    }

    public void Dispose() => _dbContext.Dispose();

    private Task<List<OutboxMessage>> GetOutboxRowsAsync() =>
        _dbContext.Set<OutboxMessage>().AsNoTracking().ToListAsync();

    // ── Integration event: outbox row stays unprocessed, no in-process dispatch, signal fired ──
    [Fact]
    public async Task SaveChangesAsync_IntegrationEvent_WritesUnprocessedRowAndSignalsWithoutDispatching()
    {
        var entity = new TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new TestIntegrationEvent());
        _dbContext.TestAggregates.Add(entity);

        await _dbContext.SaveChangesAsync();

        var rows = await GetOutboxRowsAsync();
        rows.Should().ContainSingle().Which.ProcessedOn.Should().BeNull(
            "integration events are delivered by the outbox processor via IMessageBus, never in-process");
        _mockDispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockSignal.Verify(s => s.Signal(), Times.AtLeastOnce);
        entity.DomainEvents.Should().BeEmpty();
    }

    // ── Local event: dispatched in-process and its row marked processed ──
    [Fact]
    public async Task SaveChangesAsync_LocalEvent_DispatchesInProcessAndMarksRowProcessed()
    {
        var entity = new TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new TestLocalEvent("local"));
        _dbContext.TestAggregates.Add(entity);

        await _dbContext.SaveChangesAsync();

        var rows = await GetOutboxRowsAsync();
        rows.Should().ContainSingle().Which.ProcessedOn.Should().NotBeNull(
            "a successfully dispatched local event's outbox row is only a safety net and must be marked processed");
        _mockDispatcher.Verify(
            d => d.DispatchAsync(
                It.Is<IEnumerable<IDomainEvent>>(events => events.Take(2).Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Mixed events on one aggregate: each event routes by its own kind ──
    [Fact]
    public async Task SaveChangesAsync_MixedEvents_RoutesEachEventByKind()
    {
        var entity = new TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new TestLocalEvent("local"));
        entity.AddDomainEvent(new TestIntegrationEvent());
        _dbContext.TestAggregates.Add(entity);

        IDomainEvent[]? dispatched = null;
        _mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IDomainEvent>, CancellationToken>((events, _) => dispatched = [.. events])
            .Returns(Task.CompletedTask);

        await _dbContext.SaveChangesAsync();

        var rows = await GetOutboxRowsAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.EventType.Contains(nameof(TestIntegrationEvent), StringComparison.Ordinal))
            .ProcessedOn.Should().BeNull();
        rows.Single(r => r.EventType.Contains(nameof(TestLocalEvent), StringComparison.Ordinal))
            .ProcessedOn.Should().NotBeNull();

        dispatched.Should().NotBeNull();
        dispatched.Should().ContainSingle("only the local event may be dispatched in-process")
            .Which.Should().BeOfType<TestLocalEvent>();
    }

    // ── Sync path: events cleared, rows left unprocessed, no dispatch ──
    [Fact]
    public void SaveChanges_Sync_ClearsEventsAndLeavesRowsUnprocessed()
    {
        var entity = new TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new TestLocalEvent("sync"));
        _dbContext.TestAggregates.Add(entity);

        _dbContext.SaveChanges();

        entity.DomainEvents.Should().BeEmpty(
            "the sync path must clear captured events so a later async save cannot re-capture them");
        var rows = _dbContext.Set<OutboxMessage>().AsNoTracking().ToList();
        rows.Should().ContainSingle().Which.ProcessedOn.Should().BeNull(
            "the sync path cannot await the dispatcher; the outbox processor delivers instead");
        _mockDispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockSignal.Verify(s => s.Signal(), Times.AtLeastOnce);
    }

    // ── Sync save followed by async save: no duplicate outbox rows ──
    [Fact]
    public async Task SaveChanges_SyncThenAsyncSave_DoesNotDuplicateOutboxRows()
    {
        var entity = new TestAggregate { Id = 1, Name = "Test" };
        entity.AddDomainEvent(new TestLocalEvent("sync"));
        _dbContext.TestAggregates.Add(entity);
#pragma warning disable CA1849, S6966 // The synchronous save path IS the behavior under test.
        _dbContext.SaveChanges();
#pragma warning restore CA1849, S6966

        entity.Name = "Changed";
        await _dbContext.SaveChangesAsync();

        var rows = await GetOutboxRowsAsync();
        rows.Should().ContainSingle("the sync save cleared the aggregate's events, so the async save must not re-capture them");
        rows[0].ProcessedOn.Should().BeNull();
        _mockDispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Test doubles ──
    public sealed record TestLocalEvent(string Data) : BaseDomainEvent;

    public sealed record TestIntegrationEvent : BaseDomainEvent, IIntegrationEvent;

    public sealed class TestAggregate : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class OutboxRoutingTestDbContext : ApplicationDbContext
    {
        public DbSet<TestAggregate> TestAggregates => Set<TestAggregate>();

        internal override bool SupportsOutbox => true;

        private OutboxRoutingTestDbContext(DbContextOptions<OutboxRoutingTestDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static OutboxRoutingTestDbContext Create(DomainEventSaveChangesInterceptor domainEventInterceptor)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(domainEventInterceptor);
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<OutboxRoutingTestDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new OutboxRoutingTestDbContext(options, sp);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestAggregate>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Name);
                e.Property(x => x.RowVersion).IsConcurrencyToken();
            });
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

    private sealed class NullAssemblyProvider : Application.Interfaces.Infrastructure.IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
