using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Outbox.Processing;

/// <summary>
/// The claim lease covers a whole batch, so the processor renews it lazily: before each row is
/// dispatched it renews only when less than half of <c>Outbox:LeaseSeconds</c> remains, one
/// token-guarded update re-leasing every not-yet-dispatched row, and a row whose token no longer
/// matches is skipped instead of dispatched. The clock is a <see cref="FakeTimeProvider"/> advanced
/// from inside a dispatch, so "the lease ran down" is an explicit step rather than a timing accident.
/// </summary>
public sealed class OutboxProcessorLeaseRenewalTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly OutboxSettings _settings = new();
    private readonly SqliteConnection _connection;
    private readonly LeaseTestDbContext _dbContext;
    private readonly Mock<IDomainEventDispatcher> _dispatcher = new();
    private readonly FakeTimeProvider _timeProvider = new(Now);
    private readonly OutboxProcessor _sut;

    public OutboxProcessorLeaseRenewalTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var contextServices = new ServiceCollection();
        contextServices.AddSingleton<TimeProvider>(_timeProvider);
        contextServices.AddSingleton(_dispatcher.Object);
        contextServices.AddSingleton(new AuditSaveChangesInterceptor(_timeProvider));
        contextServices.AddSingleton(new DomainEventSaveChangesInterceptor(
            _dispatcher.Object, NullLogger<DomainEventSaveChangesInterceptor>.Instance, Mock.Of<IOutboxSignal>(), timeProvider: _timeProvider));
        contextServices.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
        ServiceProvider contextProvider = contextServices.BuildServiceProvider();

        _dbContext = new LeaseTestDbContext(
            new DbContextOptionsBuilder<LeaseTestDbContext>().UseSqlite(_connection).Options,
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
            Options.Create(_settings),
            Mock.Of<IOutboxSignal>(),
            new FrameworkTableTargets(registry.Object, resolver.Object),
            _timeProvider);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _dbContext.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task FreshLease_IsNotRenewedBetweenRows()
    {
        var (first, second, third) = await SeedThreeRowsAsync();
        var claimLease = Now.UtcDateTime.AddSeconds(_settings.LeaseSeconds);

        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        var persisted = await ReadAllAsync();
        foreach (var id in new[] { first, second, third })
        {
            persisted[id].ProcessedOn.Should().NotBeNull();
            persisted[id].LockedUntil.Should().Be(claimLease, "the lease never ran below half, so nothing renewed it");
        }
    }

    [Fact]
    public async Task LeasePastHalfway_RenewsTheRemainingRows()
    {
        var (first, second, third) = await SeedThreeRowsAsync();
        var claimLease = Now.UtcDateTime.AddSeconds(_settings.LeaseSeconds);
        var advance = TimeSpan.FromSeconds(_settings.LeaseSeconds * 0.6);

        var calls = 0;
        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (++calls == 1)
                {
                    _timeProvider.Advance(advance);
                }

                return Task.CompletedTask;
            });

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        var renewedLease = Now.UtcDateTime.Add(advance).AddSeconds(_settings.LeaseSeconds);
        var persisted = await ReadAllAsync();
        persisted[first].ProcessedOn.Should().NotBeNull();
        persisted[second].ProcessedOn.Should().NotBeNull();
        persisted[third].ProcessedOn.Should().NotBeNull();
        persisted[first].LockedUntil.Should().Be(claimLease, "row 1 was dispatched before the renewal");
        persisted[second].LockedUntil.Should().Be(renewedLease);
        persisted[third].LockedUntil.Should().Be(renewedLease);
    }

    [Fact]
    public async Task RenewalThatFindsARowTakenOver_SkipsThatRow()
    {
        var (first, second, third) = await SeedThreeRowsAsync();
        var otherOwner = Guid.NewGuid();
        var advance = TimeSpan.FromSeconds(_settings.LeaseSeconds * 0.6);

        var calls = 0;
        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IDomainEvent>, CancellationToken>(async (_, ct) =>
            {
                if (++calls == 1)
                {
                    _timeProvider.Advance(advance);
                    await _dbContext.Set<OutboxMessage>()
                        .Where(m => m.Id == third)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.LockToken, otherOwner), ct);
                }
            });

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        var persisted = await ReadAllAsync();
        persisted[first].ProcessedOn.Should().NotBeNull();
        persisted[second].ProcessedOn.Should().NotBeNull();
        persisted[third].ProcessedOn.Should().BeNull("the renewal found the row under another owner, so it is never dispatched");
        persisted[third].LockToken.Should().Be(otherOwner);
        _dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    private async Task<(Guid First, Guid Second, Guid Third)> SeedThreeRowsAsync()
    {
        OutboxMessage first = EligibleMessage(Now.UtcDateTime.AddMinutes(-15));
        OutboxMessage second = EligibleMessage(Now.UtcDateTime.AddMinutes(-10));
        OutboxMessage third = EligibleMessage(Now.UtcDateTime.AddMinutes(-5));
        await _dbContext.Set<OutboxMessage>().AddRangeAsync(first, second, third);
        await _dbContext.SaveChangesAsync();
        return (first.Id, second.Id, third.Id);
    }

    private Task<Dictionary<Guid, OutboxMessage>> ReadAllAsync() =>
        _dbContext.Set<OutboxMessage>().AsNoTracking().ToDictionaryAsync(m => m.Id);

    private static OutboxMessage EligibleMessage(DateTime occurredOn) =>
        new()
        {
            Id = Guid.NewGuid(),
            EventType = typeof(LeaseTestEvent).AssemblyQualifiedName!,
            Payload = """{"DateOccurred":"2025-01-01T00:00:00Z"}""",
            OccurredOn = occurredOn,
            ProcessedOn = null,
            RetryCount = 0,
        };

    public sealed class LeaseTestEvent : IDomainEvent
    {
        public DateTime DateOccurred { get; init; }

        public Guid MessageId { get; init; } = Guid.NewGuid();
    }

    /// <summary>SQLite context mapping only <see cref="OutboxMessage"/>.</summary>
    private sealed class LeaseTestDbContext(
        DbContextOptions options,
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider)
        : ApplicationDbContext(options, serviceProvider, assemblyProvider, TestPhysicalDataSources.Sqlite())
    {
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
