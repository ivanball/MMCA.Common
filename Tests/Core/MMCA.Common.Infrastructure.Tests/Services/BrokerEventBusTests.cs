using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Infrastructure.Settings;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Services;

/// <summary>
/// Tests for <see cref="BrokerEventBus"/>: persist to the outbox, signal the processor,
/// and return WITHOUT dispatching in-process (the OutboxProcessor owns delivery in broker
/// mode). Complements InProcessEventBusTests / InProcessEventBusOutboxTests, which cover
/// the monolith implementation that does dispatch synchronously.
/// </summary>
public sealed class BrokerEventBusTests
{
    // ── Mocks ──
    private sealed record Mocks(
        Mock<IDbContextFactory> DbContextFactory,
        Mock<IOutboxSignal> OutboxSignal,
        Mock<IDataSourceResolver> Resolver);

    // ── Factory ──
    private static (BrokerEventBus Sut, Mocks Mocks) CreateSut(
        ApplicationDbContext? context = null,
        OutboxSettings? settings = null)
    {
        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory
            .Setup(x => x.GetDbContext(It.IsAny<DataSourceKey>()))
            .Returns(context!);

        var outboxSignal = new Mock<IOutboxSignal>();

        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(x => x.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        var sut = new BrokerEventBus(
            dbContextFactory.Object,
            outboxSignal.Object,
            resolver.Object,
            Options.Create(settings ?? new OutboxSettings { DataSource = DataSource.SQLServer }));

        return (sut, new Mocks(dbContextFactory, outboxSignal, resolver));
    }

    // ── Null guard: single event ──
    [Fact]
    public async Task PublishAsync_NullEvent_ThrowsArgumentNullException()
    {
        var (sut, _) = CreateSut();

        Func<Task> act = () => sut.PublishAsync((IIntegrationEvent)null!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("integrationEvent");
    }

    // ── Null guard: batch overload ──
    [Fact]
    public async Task PublishAsync_NullEventCollection_ThrowsArgumentNullException()
    {
        var (sut, _) = CreateSut();

        Func<Task> act = () => sut.PublishAsync((IEnumerable<IIntegrationEvent>)null!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("integrationEvents");
    }

    // ── Outbox-capable target: entry persisted and left PENDING (no in-process dispatch) ──
    [Fact]
    public async Task PublishAsync_OutboxCapableTarget_PersistsPendingOutboxEntry()
    {
        await using var context = TestOutboxContext.Create();
        var (sut, _) = CreateSut(context);
        var integrationEvent = new TestIntegrationEvent { DateOccurred = DateTime.UtcNow };

        await sut.PublishAsync(integrationEvent, CancellationToken.None);

        List<OutboxMessage> messages = await context.Set<OutboxMessage>().ToListAsync();
        messages.Should().ContainSingle();
        messages[0].ProcessedOn.Should().BeNull(
            "broker mode must leave delivery to the OutboxProcessor, so the entry stays pending");
        messages[0].EventType.Should().Contain(nameof(TestIntegrationEvent));
        messages[0].Payload.Should().Contain(
            integrationEvent.MessageId.ToString(),
            "the serialized payload must carry the original event data");
    }

    // ── Outbox-capable target: processor is woken exactly once per event ──
    [Fact]
    public async Task PublishAsync_OutboxCapableTarget_SignalsProcessorAfterSave()
    {
        await using var context = TestOutboxContext.Create();
        var (sut, mocks) = CreateSut(context);

        await sut.PublishAsync(new TestIntegrationEvent { DateOccurred = DateTime.UtcNow }, CancellationToken.None);

        mocks.OutboxSignal.Verify(s => s.Signal(), Times.Once);
    }

    // ── The configured Outbox:DataSource / Outbox:DatabaseName pair drives target resolution ──
    [Fact]
    public async Task PublishAsync_ResolvesConfiguredOutboxTarget()
    {
        await using var context = TestOutboxContext.Create();
        var settings = new OutboxSettings { DataSource = DataSource.Sqlite, DatabaseName = "Conference" };
        var (sut, mocks) = CreateSut(context, settings);

        await sut.PublishAsync(new TestIntegrationEvent { DateOccurred = DateTime.UtcNow }, CancellationToken.None);

        mocks.Resolver.Verify(r => r.ResolveLogical(DataSource.Sqlite, "Conference"), Times.Once);
        mocks.DbContextFactory.Verify(f => f.GetDbContext(DataSourceKey.Default(DataSource.Sqlite)), Times.Once);
    }

    // ── Non-outbox target: loud misconfiguration failure, nothing signalled ──
    [Fact]
    public async Task PublishAsync_NonOutboxTarget_ThrowsInvalidOperationExceptionAndDoesNotSignal()
    {
        await using var context = TestNonOutboxContext.Create();
        var (sut, mocks) = CreateSut(context);

        Func<Task> act = () => sut.PublishAsync(new TestIntegrationEvent { DateOccurred = DateTime.UtcNow });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not support OutboxMessage*");
        mocks.OutboxSignal.Verify(s => s.Signal(), Times.Never);
    }

    // ── Batch: each event is persisted and signalled individually ──
    [Fact]
    public async Task PublishBatch_PersistsEachEventAndSignalsPerEvent()
    {
        await using var context = TestOutboxContext.Create();
        var (sut, mocks) = CreateSut(context);
        var event1 = new TestIntegrationEvent { DateOccurred = DateTime.UtcNow };
        var event2 = new TestIntegrationEvent { DateOccurred = DateTime.UtcNow };

        await sut.PublishAsync([event1, event2], CancellationToken.None);

        List<OutboxMessage> messages = await context.Set<OutboxMessage>().ToListAsync();
        messages.Should().HaveCount(2);
        messages.Should().AllSatisfy(m => m.ProcessedOn.Should().BeNull());
        mocks.OutboxSignal.Verify(s => s.Signal(), Times.Exactly(2));
    }

    // ── Empty batch: no persistence, no signal ──
    [Fact]
    public async Task PublishBatch_EmptyCollection_DoesNotTouchOutboxOrSignal()
    {
        await using var context = TestOutboxContext.Create();
        var (sut, mocks) = CreateSut(context);

        await sut.PublishAsync(Array.Empty<IIntegrationEvent>(), CancellationToken.None);

        List<OutboxMessage> messages = await context.Set<OutboxMessage>().ToListAsync();
        messages.Should().BeEmpty();
        mocks.OutboxSignal.Verify(s => s.Signal(), Times.Never);
    }

    // ── Cancellation: a cancelled token aborts the save and never signals ──
    [Fact]
    public async Task PublishAsync_PreCancelledToken_PropagatesCancellationWithoutSignaling()
    {
        await using var context = TestOutboxContext.Create();
        var (sut, mocks) = CreateSut(context);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => sut.PublishAsync(new TestIntegrationEvent { DateOccurred = DateTime.UtcNow }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        mocks.OutboxSignal.Verify(s => s.Signal(), Times.Never);
    }

    // ── Test helpers ──
    public sealed class TestIntegrationEvent : IIntegrationEvent
    {
        public DateTime DateOccurred { get; init; }

        public Guid MessageId { get; init; } = Guid.NewGuid();
    }

    /// <summary>
    /// A test <see cref="ApplicationDbContext"/> subclass with outbox support enabled,
    /// used to observe what <see cref="BrokerEventBus"/> persists.
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
            IServiceProvider sp = BuildContextServices();

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

    /// <summary>
    /// A test <see cref="ApplicationDbContext"/> subclass with outbox support disabled,
    /// used to exercise the misconfiguration guard in <see cref="BrokerEventBus"/>.
    /// </summary>
    private sealed class TestNonOutboxContext : ApplicationDbContext
    {
        internal override bool SupportsOutbox => false;

        private TestNonOutboxContext(DbContextOptions<TestNonOutboxContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static TestNonOutboxContext Create()
        {
            IServiceProvider sp = BuildContextServices();

            var options = new DbContextOptionsBuilder<TestNonOutboxContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new TestNonOutboxContext(options, sp);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }
    }

    private static ServiceProvider BuildContextServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AuditSaveChangesInterceptor>(_ =>
            new AuditSaveChangesInterceptor(TimeProvider.System));
        services.AddSingleton<DomainEventSaveChangesInterceptor>(_ =>
        {
            var dispatcher = new Mock<IDomainEventDispatcher>();
            var logger = new Mock<Microsoft.Extensions.Logging.ILogger<DomainEventSaveChangesInterceptor>>();
            var outboxSignal = new Mock<IOutboxSignal>();
            return new DomainEventSaveChangesInterceptor(dispatcher.Object, logger.Object, outboxSignal.Object);
        });
        services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
        return services.BuildServiceProvider();
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}
