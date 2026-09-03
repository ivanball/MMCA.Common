using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Administration;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Messaging;

/// <summary>
/// Tests for the outbox-supported path in <see cref="InProcessEventBus"/>,
/// complementing InProcessEventBusTests which covers the non-outbox path.
/// </summary>
public sealed class InProcessEventBusOutboxTests : IDisposable
{
    private readonly Mock<IDbContextFactory> _mockDbContextFactory = new();
    private readonly Mock<IDomainEventDispatcher> _mockDispatcher = new();
    private readonly Mock<IDataSourceResolver> _mockResolver = new();
    private readonly OutboxSettings _outboxSettings = new() { DataSource = DataSource.SQLServer };
    private readonly TestOutboxContext _testContext;
    private readonly InProcessEventBus _sut;

    public InProcessEventBusOutboxTests()
    {
        _testContext = TestOutboxContext.Create();
        _mockDbContextFactory
            .Setup(x => x.GetDbContext(It.IsAny<DataSourceKey>()))
            .Returns(_testContext);
        _mockResolver
            .Setup(x => x.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        IOptions<OutboxSettings> options = Options.Create(_outboxSettings);
        _sut = new InProcessEventBus(_mockDbContextFactory.Object, _mockDispatcher.Object, _mockResolver.Object, options);
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

    // ── Outbox disabled: the direct-dispatch branch, no rows, no save ──
    // This is the small-application floor: an in-process host pays for no outbox table, no
    // processor and no cleanup service, and the event still reaches its handlers synchronously.
    [Fact]
    public async Task PublishAsync_WithOutboxDisabled_DispatchesDirectlyAndWritesNoRows()
    {
        var sut = new InProcessEventBus(
            _mockDbContextFactory.Object,
            _mockDispatcher.Object,
            _mockResolver.Object,
            Options.Create(_outboxSettings),
            timeProvider: null,
            Options.Create(new MessageBusSettings { EnableOutbox = false }));
        var integrationEvent = new TestIntegrationEvent { DateOccurred = DateTime.UtcNow };

        await sut.PublishAsync(integrationEvent, CancellationToken.None);

        _mockDispatcher.Verify(
            x => x.DispatchAsync(
                It.Is<IEnumerable<IDomainEvent>>(events => events.Contains(integrationEvent)),
                CancellationToken.None),
            Times.Once);
        List<OutboxMessage> messages = await _testContext.Set<OutboxMessage>().ToListAsync();
        messages.Should().BeEmpty("with the outbox off there is no processor to drain a row, so writing one only costs a round trip");
    }

    // ── Unset options keep the outbox path ──
    // The constructor parameter is optional so an existing host (and every direct construction in a
    // test) keeps the previous behaviour: the opt-out is only ever taken because configuration said so.
    [Fact]
    public async Task PublishAsync_WithNoMessageBusOptions_KeepsWritingOutboxRows()
    {
        var integrationEvent = new TestIntegrationEvent { DateOccurred = DateTime.UtcNow };

        await _sut.PublishAsync(integrationEvent, CancellationToken.None);

        List<OutboxMessage> messages = await _testContext.Set<OutboxMessage>().ToListAsync();
        messages.Should().ContainSingle();
    }

    public sealed class TestIntegrationEvent : IIntegrationEvent
    {
        public DateTime DateOccurred { get; init; }

        public Guid MessageId { get; init; } = Guid.NewGuid();
    }

    /// <summary>
    /// A test <see cref="ApplicationDbContext"/> subclass with outbox support enabled,
    /// used to test the outbox-persisting path of <see cref="InProcessEventBus"/>.
    /// </summary>
    private sealed class TestOutboxContext : ApplicationDbContext
    {
        internal override bool SupportsOutbox => true;

        private TestOutboxContext(DbContextOptions<TestOutboxContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
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
                var outboxSignal = new Mock<MMCA.Common.Infrastructure.Persistence.Outbox.Processing.IOutboxSignal>();
                return new DomainEventSaveChangesInterceptor(dispatcher.Object, logger.Object, outboxSignal.Object);
            });
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
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
