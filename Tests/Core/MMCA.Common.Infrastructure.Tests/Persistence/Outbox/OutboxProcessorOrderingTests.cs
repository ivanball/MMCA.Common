using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Outbox;

/// <summary>
/// Ordered delivery (<see cref="OutboxMessage.OrderingKey"/>): rows sharing a key must reach the bus
/// one at a time, in <c>OccurredOn</c> order, and that guarantee must survive the two things a
/// batch-local sort cannot cover: a successor arriving in a LATER batch than its predecessor, and a
/// second processor replica polling the same table at the same moment.
/// <para>
/// The harness drives <c>ProcessPendingMessagesAsync</c> directly over an in-memory SQLite database
/// with a <see cref="FakeTimeProvider"/>, so "a later batch" is an explicit second call rather than
/// a timing accident, and a lease held by another replica is a seeded column value.
/// </para>
/// </summary>
public sealed class OutboxProcessorOrderingTests : IDisposable
{
    private const string BoomMarker = "boom";

    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly OrderingTestContext _context;
    private readonly ServiceProvider _rootProvider;
    private readonly Mock<IDomainEventDispatcher> _dispatcher = new();
    private readonly FakeTimeProvider _timeProvider = new(Now);
    private readonly OutboxProcessor _sut;

    public OutboxProcessorOrderingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = OrderingTestContext.Create(_connection);

        // A dispatch of the event marked "boom" fails; everything else succeeds. That is what lets a
        // test hold ONE row in the retrying state and ask what happens to its key-mates.
        _dispatcher
            .Setup(d => d.DispatchAsync(
                It.Is<IEnumerable<IDomainEvent>>(events => events.OfType<OrderedTestEvent>()
                    .Any(e => string.Equals(e.Marker, BoomMarker, StringComparison.Ordinal))),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dispatch failed"));

        var contextFactory = new Mock<IDbContextFactory>();
        contextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(_context);

        var services = new ServiceCollection();
        services.AddSingleton(contextFactory.Object);
        services.AddSingleton(_dispatcher.Object);
        services.AddSingleton(Mock.Of<IMessageBus>());
        _rootProvider = services.BuildServiceProvider();

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        _sut = new OutboxProcessor(
            _rootProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(new OutboxSettings { MaxRetries = 3 }),
            Mock.Of<IOutboxSignal>(),
            registry.Object,
            resolver.Object,
            _timeProvider);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _context.Dispose();
        _rootProvider.Dispose();
        _connection.Dispose();
    }

    // ── Serial per key: the successor waits for the predecessor's cycle to finish ──
    [Fact]
    public async Task SameOrderingKey_DispatchesOneRowPerCycle_InOccurredOnOrder()
    {
        OutboxMessage first = Row("order-1", minutesAgo: 30);
        OutboxMessage second = Row("order-1", minutesAgo: 20);
        await SeedAsync(first, second);

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        (await ProcessedIdsAsync()).Should().BeEquivalentTo(
            [first.Id],
            "a cycle may carry only the earliest row of any one ordering key");

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        (await ProcessedIdsAsync()).Should().BeEquivalentTo(
            [first.Id, second.Id],
            "the successor becomes claimable as soon as its predecessor is processed");
    }

    // ── Cross-replica: a row held under another replica's lease still blocks its key ──
    [Fact]
    public async Task KeyedRow_IsNotDispatched_WhileAnEarlierRowWithTheSameKeyIsLeasedByAnotherReplica()
    {
        // The predecessor is not even a candidate for this cycle (an unexpired lease hides it from
        // the fetch), so nothing in this batch could order the successor behind it. Only the claim's
        // NOT EXISTS can, which is exactly why the guard lives in the database.
        OutboxMessage predecessor = Row("cart-9", minutesAgo: 30);
        predecessor.LockedUntil = Now.UtcDateTime.AddMinutes(5);
        predecessor.LockToken = Guid.NewGuid();
        OutboxMessage successor = Row("cart-9", minutesAgo: 20);
        await SeedAsync(predecessor, successor);

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        (await ProcessedIdsAsync()).Should().BeEmpty(
            "the successor must wait for a predecessor another replica is still delivering");
    }

    // ── Head-of-line: a failing predecessor holds its key until it succeeds or dies ──
    [Fact]
    public async Task KeyedRow_IsNotDispatched_WhileItsPredecessorIsBackingOffFromAFailure()
    {
        OutboxMessage predecessor = Row("invoice-7", minutesAgo: 30, marker: BoomMarker);
        OutboxMessage successor = Row("invoice-7", minutesAgo: 20);
        await SeedAsync(predecessor, successor);

        // Cycle 1: the predecessor fails and re-leases itself for its backoff.
        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        OutboxMessage failed = await ReloadAsync(predecessor.Id);
        failed.RetryCount.Should().Be(1);
        failed.ProcessedOn.Should().BeNull();

        // Cycle 2, same instant: the predecessor is leased out of the fetch, so the successor is the
        // only candidate. It must still be refused.
        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        (await ReloadAsync(successor.Id)).ProcessedOn.Should().BeNull(
            "a retrying predecessor blocks its key: delivering past it would reorder the stream");
    }

    // ── Escape hatch: an exhausted predecessor stops blocking, so one poison event cannot freeze a key ──
    [Fact]
    public async Task DeadLetteredPredecessor_NoLongerBlocksItsKey()
    {
        OutboxMessage exhausted = Row("shipment-3", minutesAgo: 30);
        exhausted.RetryCount = 3; // MaxRetries
        exhausted.LastError = "gave up";
        OutboxMessage successor = Row("shipment-3", minutesAgo: 20);
        await SeedAsync(exhausted, successor);

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        (await ProcessedIdsAsync()).Should().BeEquivalentTo(
            [successor.Id],
            "a dead letter is out of the delivery stream, so it must not hold its key hostage forever");
    }

    // ── Unkeyed rows keep the old fully parallel behavior ──
    [Fact]
    public async Task RowsWithoutAnOrderingKey_AreUnaffected_AndAllDispatchInOneCycle()
    {
        OutboxMessage first = Row(orderingKey: null, minutesAgo: 30);
        OutboxMessage second = Row(orderingKey: null, minutesAgo: 20);
        OutboxMessage third = Row(orderingKey: null, minutesAgo: 10);
        await SeedAsync(first, second, third);

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        (await ProcessedIdsAsync()).Should().BeEquivalentTo(
            [first.Id, second.Id, third.Id],
            "ordering is opt-in; without a key the outbox drains the batch as it always did");
    }

    // ── Keys are independent: one slow key must not serialize the whole outbox ──
    [Fact]
    public async Task DifferentOrderingKeys_DoNotBlockEachOther()
    {
        OutboxMessage blockedKeyHead = Row("order-1", minutesAgo: 30, marker: BoomMarker);
        OutboxMessage blockedKeyTail = Row("order-1", minutesAgo: 25);
        OutboxMessage otherKey = Row("order-2", minutesAgo: 20);
        await SeedAsync(blockedKeyHead, blockedKeyTail, otherKey);

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        (await ProcessedIdsAsync()).Should().BeEquivalentTo(
            [otherKey.Id],
            "a failing key blocks only its own stream");
    }

    // ── Helpers ──
    private static OutboxMessage Row(string? orderingKey, int minutesAgo, string marker = "ok") => new()
    {
        Id = Guid.NewGuid(),
        EventType = typeof(OrderedTestEvent).AssemblyQualifiedName!,
        Payload = $$"""{"DateOccurred":"2025-06-01T00:00:00Z","Marker":"{{marker}}"}""",
        OccurredOn = Now.UtcDateTime.AddMinutes(-minutesAgo),
        OrderingKey = orderingKey,
    };

    private async Task SeedAsync(params OutboxMessage[] messages)
    {
        _context.Set<OutboxMessage>().AddRange(messages);
        await _context.SaveChangesAsync(CancellationToken.None);
        _context.ChangeTracker.Clear();
    }

    private async Task<List<Guid>> ProcessedIdsAsync() =>
        await _context.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.ProcessedOn != null)
            .Select(m => m.Id)
            .ToListAsync(CancellationToken.None);

    private async Task<OutboxMessage> ReloadAsync(Guid id) =>
        await _context.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == id, CancellationToken.None);

    /// <summary>A domain event carrying a marker the dispatcher double can fail on selectively.</summary>
    public sealed class OrderedTestEvent : IDomainEvent
    {
        public DateTime DateOccurred { get; init; }

        public Guid MessageId { get; init; } = Guid.NewGuid();

        public string Marker { get; init; } = string.Empty;
    }

    /// <summary>
    /// A test <see cref="ApplicationDbContext"/> mapping <see cref="OutboxMessage"/> only. The
    /// ordering column needs no explicit configuration here: EF maps it by convention, which is also
    /// how it reaches a consumer's migration.
    /// </summary>
    private sealed class OrderingTestContext : ApplicationDbContext
    {
        internal override bool SupportsOutbox => true;

        private OrderingTestContext(DbContextOptions<OrderingTestContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NoAssemblies(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static OrderingTestContext Create(SqliteConnection connection)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(_ => new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            ServiceProvider provider = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<OrderingTestContext>()
                .UseSqlite(connection)
                .Options;

            var context = new OrderingTestContext(options, provider);
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
                entity.Property(e => e.OrderingKey).HasMaxLength(200);
            });

        private sealed class NoAssemblies : IEntityConfigurationAssemblyProvider
        {
            public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
        }
    }
}
