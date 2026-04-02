using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Infrastructure.Settings;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Services;

/// <summary>
/// Tests for the outbox-supported path in <see cref="InProcessEventBus"/>,
/// complementing InProcessEventBusTests which covers the non-outbox path.
/// </summary>
public sealed class InProcessEventBusOutboxTests : IDisposable
{
    private readonly Mock<IDbContextFactory> _mockDbContextFactory = new();
    private readonly Mock<IDomainEventDispatcher> _mockDispatcher = new();
    private readonly OutboxSettings _outboxSettings = new() { DataSource = DataSource.SQLServer };
    private readonly TestOutboxContext _testContext;
    private readonly InProcessEventBus _sut;

    public InProcessEventBusOutboxTests()
    {
        _testContext = TestOutboxContext.Create();
        _mockDbContextFactory
            .Setup(x => x.GetDbContext(It.IsAny<DataSource>()))
            .Returns(_testContext);

        IOptions<OutboxSettings> options = Options.Create(_outboxSettings);
        _sut = new InProcessEventBus(_mockDbContextFactory.Object, _mockDispatcher.Object, options);
    }

    public void Dispose() => _testContext.Dispose();

    // ── Outbox-supported path: persists to outbox, dispatches, marks processed ──
    [Fact]
    public async Task PublishAsync_WithOutboxSupport_PersistsToOutboxAndDispatches()
    {
        var integrationEvent = new TestIntegrationEvent { DateOccurred = DateTime.UtcNow };

        _mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.PublishAsync(integrationEvent, CancellationToken.None);

        // Verify dispatcher was called
        _mockDispatcher.Verify(
            x => x.DispatchAsync(
                It.Is<IEnumerable<IDomainEvent>>(events => events.Contains(integrationEvent)),
                CancellationToken.None),
            Times.Once);

        // Verify outbox message was persisted and marked as processed
        List<OutboxMessage> messages = await _testContext.Set<OutboxMessage>().ToListAsync();
        messages.Should().ContainSingle();
        messages[0].ProcessedOn.Should().NotBeNull("event was dispatched successfully so outbox entry should be marked processed");
        messages[0].EventType.Should().Contain(nameof(TestIntegrationEvent));
    }

    // ── Outbox-supported path: batch dispatches all events ──
    [Fact]
    public async Task PublishBatch_WithOutboxSupport_PersistsEachToOutbox()
    {
        var event1 = new TestIntegrationEvent { DateOccurred = DateTime.UtcNow };
        var event2 = new TestIntegrationEvent { DateOccurred = DateTime.UtcNow };

        _mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.PublishAsync([event1, event2], CancellationToken.None);

        List<OutboxMessage> messages = await _testContext.Set<OutboxMessage>().ToListAsync();
        messages.Should().HaveCount(2);
        messages.Should().AllSatisfy(m => m.ProcessedOn.Should().NotBeNull());
    }

    public sealed class TestIntegrationEvent : IIntegrationEvent
    {
        public DateTime DateOccurred { get; init; }
    }

    /// <summary>
    /// A test <see cref="ApplicationDbContext"/> subclass with outbox support enabled,
    /// used to test the outbox-persisting path of <see cref="InProcessEventBus"/>.
    /// </summary>
    private sealed class TestOutboxContext : ApplicationDbContext
    {
        internal override bool SupportsOutbox => true;

        private TestOutboxContext(DbContextOptions<TestOutboxContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider())
        {
        }

        public static TestOutboxContext Create()
        {
            var services = new ServiceCollection();
            services.AddSingleton<AuditSaveChangesInterceptor>(_ =>
                new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton<DomainEventSaveChangesInterceptor>(_ =>
            {
                var dispatcher = new Mock<IDomainEventDispatcher>();
                var logger = new Mock<Microsoft.Extensions.Logging.ILogger<DomainEventSaveChangesInterceptor>>();
                var outboxSignal = new Mock<MMCA.Common.Infrastructure.Persistence.Outbox.IOutboxSignal>();
                return new DomainEventSaveChangesInterceptor(dispatcher.Object, logger.Object, outboxSignal.Object);
            });
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<TestOutboxContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new TestOutboxContext(options, sp);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

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

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}
