using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DbContexts;

/// <summary>
/// A commit-phase failure has an unknowable outcome: the database may have applied the transaction
/// and lost only the acknowledgement. These tests pin that
/// <see cref="DbContextFactory.ExecuteInTransactionAsync{TResult}"/> reports it as
/// <see cref="TransactionCommitAmbiguousException"/> and never lets the execution strategy re-run
/// the operation on top of it. The context here hands out a strategy that retries on ANY exception,
/// which is what a raw (or naively wrapped) commit failure would have triggered.
/// </summary>
public sealed class DbContextFactoryCommitAmbiguityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Mock<IDomainEventDispatcher> _dispatcherMock = new();
    private readonly CommitFailingDbContext _dbContext;
    private readonly DbContextFactory _sut;

    public DbContextFactoryCommitAmbiguityTests()
    {
        _connection = OpenConnection();
        _dbContext = CommitFailingDbContext.Create(_connection, _dispatcherMock.Object);

        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        physicalFactory.Setup(f => f.Create(It.IsAny<DataSourceKey>())).Returns(_dbContext);

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        _sut = new DbContextFactory(
            physicalFactory.Object,
            registry.Object,
            new DefaultDataSourceResolver(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<ITenantContext>(),
            Options.Create(new TenancySettings()));
    }

    public void Dispose()
    {
        _sut.Dispose();
        _dbContext.Dispose();
        _connection.Dispose();
    }

    // ── The commit failure is wrapped, and the operation runs exactly once ──
    [Fact]
    public async Task ExecuteInTransactionAsync_CommitFails_ThrowsAmbiguityWrapperWithoutRerunningTheOperation()
    {
        var lostAcknowledgement = new TimeoutException("commit acknowledgement lost");
        _dbContext.FailCommitWith = lostAcknowledgement;
        var attempts = 0;

        var act = async () => await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                attempts++;
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                context.Set<TestAggregate>().Add(CreateAggregateWithEvent());
                await _sut.SaveChangesAsync(ct);
                return Result.Success();
            });

        var thrown = await act.Should().ThrowAsync<TransactionCommitAmbiguousException>();
        thrown.And.InnerException.Should().BeSameAs(
            lostAcknowledgement,
            "the provider's failure is the diagnostic payload, not the reported failure mode");
        attempts.Should().Be(
            1,
            "a retry would re-run every write of an operation whose commit may already be durable");

        thrown.And.CommittedSources.Should().BeEmpty(
            "nothing was committed ahead of the only source, so no part of the write is known durable");
        thrown.And.AmbiguousSource.Should().Be(
            DataSourceKey.Default(DataSource.SQLServer).ToString(),
            "with one transactional source the whole outcome is that source's outcome");
        thrown.And.RolledBackSources.Should().BeEmpty(
            "there was no later source left holding an open transaction");
    }

    // ── Nothing is delivered in-process for a commit nobody can vouch for ──
    [Fact]
    public async Task ExecuteInTransactionAsync_CommitFails_DropsTheDeferredDispatch()
    {
        _dbContext.FailCommitWith = new TimeoutException("commit acknowledgement lost");

        var act = async () => await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                context.Set<TestAggregate>().Add(CreateAggregateWithEvent());
                await _sut.SaveChangesAsync(ct);
                return Result.Success();
            });

        await act.Should().ThrowAsync<TransactionCommitAmbiguousException>();
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the outbox is the only delivery record that survives an ambiguous commit");
        _dbContext.Database.CurrentTransaction.Should().BeNull(
            "the uncommitted transaction is abandoned before the ambiguity surfaces");
    }

    // ── Control: the same retrying strategy commits and flushes normally when nothing fails ──
    [Fact]
    public async Task ExecuteInTransactionAsync_CommitSucceeds_CommitsAndFlushesAsUsual()
    {
        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                context.Set<TestAggregate>().Add(CreateAggregateWithEvent());
                await _sut.SaveChangesAsync(ct);
                return Result.Success();
            });

        result.IsSuccess.Should().BeTrue();
        (await _dbContext.Set<TestAggregate>().AsNoTracking().CountAsync()).Should().Be(1);
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Control: the strategy under test really does retry, so the exemption above is meaningful ──
    [Fact]
    public async Task ExecuteInTransactionAsync_OperationFailsBeforeCommit_IsStillRetried()
    {
        var attempts = 0;

        var act = async () => await _sut.ExecuteInTransactionAsync<Result>(
            _ =>
            {
                attempts++;
                throw new TimeoutException("transient failure inside the operation");
            });

        await act.Should().ThrowAsync<RetryLimitExceededException>();
        attempts.Should().Be(
            4,
            "one attempt plus the strategy's three retries; only the commit phase opts out of this");
    }

    // ── Sequential commits: the exception names what landed, what is unknowable, and what did not ──
    [Fact]
    public async Task ExecuteInTransactionAsync_SecondSourceCommitFails_ReportsEachSourceOutcome()
    {
        // The default SQL Server key comes first deliberately: ExecuteInTransactionAsync materializes
        // it to obtain an execution strategy, which makes it the first entry and so the first commit.
        var productsKey = DataSourceKey.Default(DataSource.SQLServer);
        var ordersKey = new DataSourceKey(DataSource.SQLServer, "Orders");
        var auditKey = new DataSourceKey(DataSource.SQLServer, "Audit");

        await using var productsConnection = OpenConnection();
        await using var ordersConnection = OpenConnection();
        await using var auditConnection = OpenConnection();

        var products = CommitFailingDbContext.Create(productsConnection, _dispatcherMock.Object);
        var orders = CommitFailingDbContext.Create(ordersConnection, _dispatcherMock.Object);
        var audit = CommitFailingDbContext.Create(auditConnection, _dispatcherMock.Object);

        var lostAcknowledgement = new TimeoutException("commit acknowledgement lost");
        orders.FailCommitWith = lostAcknowledgement;

        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        physicalFactory.Setup(f => f.Create(productsKey)).Returns(products);
        physicalFactory.Setup(f => f.Create(ordersKey)).Returns(orders);
        physicalFactory.Setup(f => f.Create(auditKey)).Returns(audit);

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        await using var sut = new DbContextFactory(
            physicalFactory.Object,
            registry.Object,
            new DefaultDataSourceResolver(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<ITenantContext>(),
            Options.Create(new TenancySettings()));

        var act = async () => await sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                // Touch order is enlistment order, which is commit order.
                sut.GetDbContext(productsKey).Set<TestAggregate>().Add(CreateAggregateWithEvent());
                sut.GetDbContext(ordersKey).Set<TestAggregate>().Add(CreateAggregateWithEvent());
                sut.GetDbContext(auditKey).Set<TestAggregate>().Add(CreateAggregateWithEvent());
                await sut.SaveChangesAsync(ct);
                return Result.Success();
            });

        var thrown = await act.Should().ThrowAsync<TransactionCommitAmbiguousException>();

        thrown.And.InnerException.Should().BeSameAs(
            lostAcknowledgement,
            "the provider's failure stays the diagnostic payload under the outcome map");
        thrown.And.CommittedSources.Should().ContainSingle()
            .Which.Should().Be(
                productsKey.ToString(),
                "the first source committed before the failure and is durable");
        thrown.And.AmbiguousSource.Should().Be(
            ordersKey.ToString(),
            "the source whose commit threw is the one nobody can vouch for");
        thrown.And.RolledBackSources.Should().ContainSingle()
            .Which.Should().Be(
                auditKey.ToString(),
                "a source never reached by the sequential commit wrote nothing that survives");
        thrown.And.Message.Should()
            .Contain("committed [" + productsKey.ToString() + "]")
            .And.Contain("ambiguous [" + ordersKey.ToString() + "]")
            .And.Contain("rolled back [" + auditKey.ToString() + "]",
                "the partial state has to be readable straight off the message in a log");

        audit.Database.CurrentTransaction.Should().BeNull(
            "the sources past the failure are rolled back before the ambiguity surfaces");
        (await products.Set<TestAggregate>().AsNoTracking().CountAsync()).Should().Be(
            1,
            "there is no two-phase commit, so an already-committed source stays committed");
        (await audit.Set<TestAggregate>().AsNoTracking().CountAsync()).Should().Be(
            0,
            "the rolled-back source is the half of the partial commit that did not land");
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    private static TestAggregate CreateAggregateWithEvent()
    {
        var aggregate = new TestAggregate { Id = 1, Name = "Test" };
        aggregate.AddDomainEvent(new TestLocalEvent());
        return aggregate;
    }

    // ── Test doubles ──
    public sealed record TestLocalEvent : BaseDomainEvent;

    public sealed class TestAggregate : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// A context whose commit can be armed to fail, and whose execution strategy retries on any
    /// exception. Together they reproduce the shape SQL Server produces in production: a transient
    /// commit error under <c>EnableRetryOnFailure</c>.
    /// </summary>
    public sealed class CommitFailingDbContext : ApplicationDbContext
    {
        private FailingDatabaseFacade? _databaseFacade;

        private CommitFailingDbContext(DbContextOptions<CommitFailingDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        /// <summary>The exception the next commit raises, or <see langword="null"/> to commit normally.</summary>
        public Exception? FailCommitWith { get; set; }

        public override DatabaseFacade Database => _databaseFacade ??= new FailingDatabaseFacade(this);

        internal override bool SupportsOutbox => true;

        public static CommitFailingDbContext Create(SqliteConnection connection, IDomainEventDispatcher dispatcher)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                dispatcher,
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<CommitFailingDbContext>()
                .UseSqlite(connection)
                .Options;

            var context = new CommitFailingDbContext(options, sp);
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

        private sealed class FailingDatabaseFacade(CommitFailingDbContext owner) : DatabaseFacade(owner)
        {
            public override void CommitTransaction()
            {
                if (owner.FailCommitWith is { } failure)
                    throw failure;

                base.CommitTransaction();
            }

            public override IExecutionStrategy CreateExecutionStrategy() => new AlwaysRetryExecutionStrategy(owner);
        }

        /// <summary>Stands in for <c>SqlServerRetryingExecutionStrategy</c>, but retries on everything.</summary>
        private sealed class AlwaysRetryExecutionStrategy(DbContext context)
            : ExecutionStrategy(context, maxRetryCount: 3, maxRetryDelay: TimeSpan.Zero)
        {
            protected override bool ShouldRetryOn(Exception exception) => true;
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
