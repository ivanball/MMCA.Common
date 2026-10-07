using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Outbox.Processing;

/// <summary>
/// The outbox stamps each row as it is delivered, through a lease-token-guarded
/// <c>ExecuteUpdate</c>, instead of collecting stamps in the change tracker for one save at the end
/// of the batch. Two multi-replica consequences are pinned here: a batch that never reaches its end
/// (a crash, or a shutdown whose last save fails) still leaves every delivered row processed, so it
/// is not redelivered when its lease expires; and a row whose lease another owner took over
/// mid-batch is never stamped by the owner that lost it.
/// <para>
/// SQLite in memory: <c>ExecuteUpdate</c> runs on SQLite exactly as the claim already does, and a
/// save that throws (<see cref="StampTestDbContext.FailSaves"/>) stands in for a batch-end save that
/// never lands. <c>ExecuteUpdate</c> does not go through <c>SaveChanges</c>, which is the point.
/// </para>
/// </summary>
public sealed class OutboxProcessorPerRowStampTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly StampTestDbContext _dbContext;
    private readonly Mock<IDomainEventDispatcher> _dispatcher = new();
    private readonly OutboxProcessor _sut;

    public OutboxProcessorPerRowStampTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var contextServices = new ServiceCollection();
        contextServices.AddSingleton(TimeProvider.System);
        contextServices.AddSingleton(_dispatcher.Object);
        contextServices.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
        contextServices.AddSingleton(new DomainEventSaveChangesInterceptor(
            _dispatcher.Object, NullLogger<DomainEventSaveChangesInterceptor>.Instance, Mock.Of<IOutboxSignal>(), timeProvider: TimeProvider.System));
        contextServices.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
        ServiceProvider contextProvider = contextServices.BuildServiceProvider();

        _dbContext = new StampTestDbContext(
            new DbContextOptionsBuilder<StampTestDbContext>().UseSqlite(_connection).Options,
            contextProvider,
            Mock.Of<IEntityConfigurationAssemblyProvider>(p => p.GetConfigurationAssemblies() == Array.Empty<Assembly>()));
        _dbContext.Database.EnsureCreated();

        var factory = new Mock<IDbContextFactory>();
        factory.Setup(f => f.GetDbContext(DataSourceKey.Default(DataSource.SQLServer))).Returns(_dbContext);

        var services = new ServiceCollection();
        services.AddSingleton(factory.Object);
        services.AddSingleton(_dispatcher.Object);
        services.AddSingleton(Mock.Of<IMessageBus>());
        ServiceProvider rootProvider = services.BuildServiceProvider();

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);
        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        _sut = new OutboxProcessor(
            rootProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(new OutboxSettings()),
            Mock.Of<IOutboxSignal>(),
            new FrameworkTableTargets(registry.Object, resolver.Object),
            TimeProvider.System);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _dbContext.Dispose();
        _connection.Dispose();
    }

    // (a) A crash after row 1 of 3: the shutdown cancels during row 2 and the database refuses the
    // batch-end save. Row 1 was delivered, so it must already be stamped in the database.
    [Fact]
    public async Task CrashAfterTheFirstRow_LeavesTheDeliveredRowProcessedInTheDatabase()
    {
        using var cts = new CancellationTokenSource();
        var (first, second, third) = await SeedThreeRowsAsync();

        var calls = 0;
        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IDomainEvent>, CancellationToken>(async (_, _) =>
            {
                if (++calls == 1)
                {
                    return;
                }

                _dbContext.FailSaves = true;
                await cts.CancelAsync();
                throw new OperationCanceledException(cts.Token);
            });

        Func<Task> act = () => _sut.ProcessPendingMessagesAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _dbContext.FailSaves = false;

        var persisted = await ReadAllAsync();
        persisted[first].ProcessedOn.Should().NotBeNull("the row was delivered, so its stamp must not depend on a later save");
        persisted[second].ProcessedOn.Should().BeNull("the interrupted row was never delivered");
        persisted[third].ProcessedOn.Should().BeNull("the third row was never attempted");
    }

    // A shutdown signalled while row 1's delivery is in flight, by a handler that finishes anyway
    // (it never observes the token). The row WAS delivered, so its stamp must still land in the
    // database, and only then does the shutdown continue.
    [Fact]
    public async Task ShutdownDuringADeliveryThatCompletesAnyway_StillLeavesTheRowProcessedInTheDatabase()
    {
        using var cts = new CancellationTokenSource();
        var (first, second, third) = await SeedThreeRowsAsync();

        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IDomainEvent>, CancellationToken>(async (_, _) => await cts.CancelAsync());

        Func<Task> act = () => _sut.ProcessPendingMessagesAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>("the shutdown continues once the outcome is recorded");

        var persisted = await ReadAllAsync();
        persisted[first].ProcessedOn.Should().NotBeNull("the delivery completed, so the shutdown must not cost the row its stamp");
        persisted[second].ProcessedOn.Should().BeNull("the shutdown stops the batch before the next row");
        persisted[third].ProcessedOn.Should().BeNull("the third row was never attempted");
    }

    // A batch whose end-of-batch save fails (the connection drops after the last publish) still
    // leaves every delivered row stamped: each stamp landed as its row was sent.
    [Fact]
    public async Task WhenTheBatchEndSaveNeverLands_EveryDeliveredRowIsStillProcessed()
    {
        var (first, second, third) = await SeedThreeRowsAsync();

        var calls = 0;
        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (++calls == 3)
                {
                    _dbContext.FailSaves = true;
                }

                return Task.CompletedTask;
            });

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);
        _dbContext.FailSaves = false;

        var persisted = await ReadAllAsync();
        persisted[first].ProcessedOn.Should().NotBeNull();
        persisted[second].ProcessedOn.Should().NotBeNull();
        persisted[third].ProcessedOn.Should().NotBeNull("the stamp is written per row, not by the batch save");
    }

    // (b) Another owner took the row over mid-batch (its lease expired and a second replica claimed
    // it, writing a new token). The original owner's stamp is guarded by its own token, so it must
    // not mark the row processed underneath the new owner.
    [Fact]
    public async Task ARowWhoseLeaseTokenChangedMidBatch_IsNotStampedByTheOriginalOwner()
    {
        var (first, second, third) = await SeedThreeRowsAsync();
        var otherOwner = Guid.NewGuid();

        var calls = 0;
        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IDomainEvent>, CancellationToken>(async (_, ct) =>
            {
                if (++calls == 1)
                {
                    // While row 1 is being delivered, another replica claims it.
                    await _dbContext.Set<OutboxMessage>()
                        .Where(m => m.Id == first)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.LockToken, otherOwner), ct);
                }
            });

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        var persisted = await ReadAllAsync();
        persisted[first].ProcessedOn.Should().BeNull("the lease belongs to another owner now; only it may stamp the row");
        persisted[first].LockToken.Should().Be(otherOwner, "the original owner must not overwrite the new owner's claim");
        persisted[second].ProcessedOn.Should().NotBeNull("rows still under this owner's lease are stamped as usual");
        persisted[third].ProcessedOn.Should().NotBeNull();
    }

    private async Task<(Guid First, Guid Second, Guid Third)> SeedThreeRowsAsync()
    {
        OutboxMessage first = EligibleMessage(DateTime.UtcNow.AddMinutes(-15));
        OutboxMessage second = EligibleMessage(DateTime.UtcNow.AddMinutes(-10));
        OutboxMessage third = EligibleMessage(DateTime.UtcNow.AddMinutes(-5));
        await _dbContext.Set<OutboxMessage>().AddRangeAsync(first, second, third);
        await _dbContext.SaveChangesAsync();
        return (first.Id, second.Id, third.Id);
    }

    private async Task<Dictionary<Guid, OutboxMessage>> ReadAllAsync() =>
        await _dbContext.Set<OutboxMessage>().AsNoTracking().ToDictionaryAsync(m => m.Id);

    private static OutboxMessage EligibleMessage(DateTime occurredOn) =>
        new()
        {
            Id = Guid.NewGuid(),
            EventType = typeof(StampTestEvent).AssemblyQualifiedName!,
            Payload = """{"DateOccurred":"2025-01-01T00:00:00Z"}""",
            OccurredOn = occurredOn,
            ProcessedOn = null,
            RetryCount = 0,
        };

    public sealed class StampTestEvent : IDomainEvent
    {
        public DateTime DateOccurred { get; init; }

        public Guid MessageId { get; init; } = Guid.NewGuid();
    }

    /// <summary>SQLite context mapping only <see cref="OutboxMessage"/>, with a switch that makes every save fail.</summary>
    private sealed class StampTestDbContext(
        DbContextOptions options,
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider)
        : ApplicationDbContext(options, serviceProvider, assemblyProvider, TestPhysicalDataSources.Sqlite())
    {
        /// <summary>When set, every save fails, standing in for a batch-end save that never lands.</summary>
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
