using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Behavioral tests for <see cref="DbContextFactory.ExecuteInTransactionAsync{TResult}"/> over a
/// real SQLite transaction: a returned failed <see cref="Result"/> rolls the transaction back
/// exactly like an exception, a success commits and only THEN flushes the deferred in-process
/// domain event dispatch, and a rollback drops the deferred dispatch entirely.
/// </summary>
public sealed class DbContextFactoryTransactionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Mock<IDomainEventDispatcher> _dispatcherMock = new();
    private readonly TransactionTestDbContext _dbContext;
    private readonly DbContextFactory _sut;

    public DbContextFactoryTransactionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = TransactionTestDbContext.Create(_connection, _dispatcherMock.Object);

        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        physicalFactory.Setup(f => f.Create(It.IsAny<DataSourceKey>())).Returns(_dbContext);

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        _sut = new DbContextFactory(
            physicalFactory.Object,
            registry.Object,
            Mock.Of<IDataSourceResolver>(),
            Mock.Of<ICurrentUserService>());
    }

    public void Dispose()
    {
        _sut.Dispose();
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private static TestAggregate CreateAggregateWithEvent()
    {
        var aggregate = new TestAggregate { Id = 1, Name = "Test" };
        aggregate.AddDomainEvent(new TestLocalEvent());
        return aggregate;
    }

    // ── Failed Result rolls the transaction back ──
    [Fact]
    public async Task ExecuteInTransactionAsync_OperationReturnsFailure_RollsBackSavedChanges()
    {
        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSource.SQLServer);
                context.Set<TestAggregate>().Add(new TestAggregate { Id = 1, Name = "Test" });
                await _sut.SaveChangesAsync(ct);
                return Result.Failure(Error.Validation("Invariant.Failed", "a later invariant failed"));
            });

        result.IsFailure.Should().BeTrue();
        (await _dbContext.Set<TestAggregate>().AsNoTracking().CountAsync())
            .Should().Be(0, "a business failure must not leave the partial mutation committed (ADR-013 atomicity)");
        _dbContext.Database.CurrentTransaction.Should().BeNull();
    }

    // ── Success commits and only then dispatches the deferred domain events ──
    [Fact]
    public async Task ExecuteInTransactionAsync_Success_CommitsThenDispatchesDeferredEvents()
    {
        var dispatchedDuringOperation = false;
        bool? transactionActiveAtDispatch = null;
        _dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => transactionActiveAtDispatch = _dbContext.Database.CurrentTransaction is not null)
            .Returns(Task.CompletedTask);

        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSource.SQLServer);
                context.Set<TestAggregate>().Add(CreateAggregateWithEvent());
                await _sut.SaveChangesAsync(ct);
                dispatchedDuringOperation = _dispatcherMock.Invocations.Any(
                    i => i.Method.Name == nameof(IDomainEventDispatcher.DispatchAsync));
                return Result.Success();
            });

        result.IsSuccess.Should().BeTrue();
        dispatchedDuringOperation.Should().BeFalse(
            "in-process dispatch must be deferred while the transaction is still open");
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        transactionActiveAtDispatch.Should().BeFalse(
            "handlers must only ever see durable, committed state");
        (await _dbContext.Set<TestAggregate>().AsNoTracking().CountAsync()).Should().Be(1);
    }

    // ── Failure/rollback drops the deferred dispatch: nothing is ever delivered in-process ──
    [Fact]
    public async Task ExecuteInTransactionAsync_OperationReturnsFailure_NeverDispatchesDeferredEvents()
    {
        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSource.SQLServer);
                context.Set<TestAggregate>().Add(CreateAggregateWithEvent());
                await _sut.SaveChangesAsync(ct);
                return Result.Failure(Error.Validation("Invariant.Failed", "a later invariant failed"));
            });

        result.IsFailure.Should().BeTrue();
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the events' outbox rows rolled back with the data, so nothing may be delivered");
        (await _dbContext.Set<OutboxMessage>().AsNoTracking().CountAsync())
            .Should().Be(0, "the outbox rows must roll back with the aggregate changes");
    }

    // ── An exception inside the operation also rolls back and drops deferred work ──
    [Fact]
    public async Task ExecuteInTransactionAsync_OperationThrows_RollsBackAndDropsDeferredEvents()
    {
        var act = async () => await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSource.SQLServer);
                context.Set<TestAggregate>().Add(CreateAggregateWithEvent());
                await _sut.SaveChangesAsync(ct);
                throw new InvalidOperationException("handler blew up");
            });

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _dbContext.Set<TestAggregate>().AsNoTracking().CountAsync()).Should().Be(0);
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Test doubles ──
    public sealed record TestLocalEvent : BaseDomainEvent;

    public sealed class TestAggregate : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class TransactionTestDbContext : ApplicationDbContext
    {
        internal override bool SupportsOutbox => true;

        private TransactionTestDbContext(DbContextOptions<TransactionTestDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static TransactionTestDbContext Create(SqliteConnection connection, IDomainEventDispatcher dispatcher)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                dispatcher,
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<TransactionTestDbContext>()
                .UseSqlite(connection)
                .Options;

            var context = new TransactionTestDbContext(options, sp);
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

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
