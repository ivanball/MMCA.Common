using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DbContexts;

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
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
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
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
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

    // ── Re-entrancy: a nested call joins the ambient transaction ──
    [Fact]
    public async Task ExecuteInTransactionAsync_Nested_DoesNotThrowAndCommitsOnce()
    {
        // The shape an ITransactional command whose handler ALSO opens a transaction produces.
        // Before this was re-entrant, the inner BeginTransaction threw InvalidOperationException
        // from EF ("already in a transaction"), which is why Store's CheckOutCommand and
        // VerifyPaymentCommand carry comments explaining they are deliberately not ITransactional.
        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async outerCt =>
            {
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                context.Set<TestAggregate>().Add(CreateAggregateWithEvent());

                return await _sut.ExecuteInTransactionAsync<Result>(
                    async innerCt =>
                    {
                        await _sut.SaveChangesAsync(innerCt);
                        return Result.Success();
                    },
                    outerCt);
            });

        result.IsSuccess.Should().BeTrue();
        (await _dbContext.Set<TestAggregate>().AsNoTracking().CountAsync()).Should().Be(1);
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the deferred flush belongs to the outermost call alone, so it must not run per nesting level");
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_NestedInnerSucceedsButOuterFails_RollsBackEverything()
    {
        // The reason an inner call must not commit: it would make the nest's earlier work durable
        // ahead of the outer scope's own decision, turning nesting into a silent partial commit.
        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async outerCt =>
            {
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                context.Set<TestAggregate>().Add(CreateAggregateWithEvent());

                var inner = await _sut.ExecuteInTransactionAsync<Result>(
                    async innerCt =>
                    {
                        await _sut.SaveChangesAsync(innerCt);
                        return Result.Success();
                    },
                    outerCt);

                inner.IsSuccess.Should().BeTrue();
                return Result.Failure(Error.Validation("Invariant.Failed", "outer scope rejected it"));
            });

        result.IsFailure.Should().BeTrue();
        (await _dbContext.Set<TestAggregate>().AsNoTracking().CountAsync()).Should().Be(
            0,
            "the inner call must not have committed the outer scope's work");
        _dispatcherMock.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Failure/rollback drops the deferred dispatch: nothing is ever delivered in-process ──
    [Fact]
    public async Task ExecuteInTransactionAsync_OperationReturnsFailure_NeverDispatchesDeferredEvents()
    {
        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
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
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
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

    // ── Commit rule: enrolled internal-command rows are flushed, any other unsaved change fails ──
    // The scheduler only ENROLLS its row while a transaction is open; before this rule the commit
    // never saved, so a command scheduled after the handler's last save was silently dropped (Store's
    // AddVariantHandler, bug-hunt L109).
    [Fact]
    public async Task ExecuteInTransactionAsync_CommandScheduledAfterTheLastSave_IsFlushedAndCommitted()
    {
        var scheduler = CreateScheduler();

        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                context.Set<TestAggregate>().Add(new TestAggregate { Id = 1, Name = "Test" });
                await _sut.SaveChangesAsync(ct);

                var scheduled = await scheduler.ScheduleAsync(new RecordingCommand("after-save"), runAt: null, ct);
                return scheduled.IsSuccess ? Result.Success() : Result.Failure(scheduled.Errors);
            });

        result.IsSuccess.Should().BeTrue();
        (await _dbContext.Set<TestAggregate>().AsNoTracking().CountAsync()).Should().Be(1);
        (await _dbContext.Set<InternalCommandMessage>().AsNoTracking().CountAsync())
            .Should().Be(1, "the enrolled row must commit with the aggregate, not be discarded at commit");
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_CommandScheduledBeforeTheSave_PersistsOneRow()
    {
        var scheduler = CreateScheduler();

        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                context.Set<TestAggregate>().Add(new TestAggregate { Id = 1, Name = "Test" });
                await scheduler.ScheduleAsync(new RecordingCommand("before-save"), runAt: null, ct);
                await _sut.SaveChangesAsync(ct);
                return Result.Success();
            });

        result.IsSuccess.Should().BeTrue();
        (await _dbContext.Set<InternalCommandMessage>().AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_CommandScheduledThenFailure_RollsTheRowBack()
    {
        var scheduler = CreateScheduler();

        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                await scheduler.ScheduleAsync(new RecordingCommand("doomed"), runAt: null, ct);
                return Result.Failure(Error.Validation("Invariant.Failed", "a later invariant failed"));
            });

        result.IsFailure.Should().BeTrue();
        (await _dbContext.Set<InternalCommandMessage>().AsNoTracking().CountAsync())
            .Should().Be(0, "a business failure must not flush the enrolled row");
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_OtherChangeLeftUnsaved_ThrowsAndRollsEverythingBack()
    {
        var act = async () => await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                context.Set<TestAggregate>().Add(new TestAggregate { Id = 1, Name = "Saved" });
                await _sut.SaveChangesAsync(ct);

                // Never saved: before the commit rule this was silently discarded while the unit
                // still reported success.
                context.Set<TestAggregate>().Add(new TestAggregate { Id = 2, Name = "Forgotten" });
                return Result.Success();
            });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unsaved*");
        (await _dbContext.Set<TestAggregate>().AsNoTracking().CountAsync())
            .Should().Be(0, "the unit fails closed, so the earlier saved work rolls back too");
    }

    // Post-commit wake: an enrolled row signals the processor once, only after the commit.
    // Inside a transaction the scheduler only enrolls its row, so before this rule the processor
    // learned of it at its next poll (300s in deployed environments), which is what stalled Store's
    // cross-service tier once AddVariantCommand became ITransactional.
    [Fact]
    public async Task ExecuteInTransactionAsync_CommandsEnrolled_SignalsOnceAfterTheCommit()
    {
        var signal = new Mock<IInternalCommandSignal>();
        bool? transactionActiveAtSignal = null;
        signal.Setup(s => s.Signal())
            .Callback(() => transactionActiveAtSignal = _dbContext.Database.CurrentTransaction is not null);
        var scheduler = CreateScheduler(signal.Object);
        var signalledDuringOperation = false;

        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                context.Set<TestAggregate>().Add(new TestAggregate { Id = 1, Name = "Test" });
                await scheduler.ScheduleAsync(new RecordingCommand("first"), runAt: null, ct);
                await _sut.SaveChangesAsync(ct);
                await scheduler.ScheduleAsync(new RecordingCommand("second"), runAt: null, ct);
                signalledDuringOperation = signal.Invocations.Count > 0;
                return Result.Success();
            });

        result.IsSuccess.Should().BeTrue();
        signalledDuringOperation.Should().BeFalse("a wake before the commit only polls an uncommitted transaction");
        signal.Verify(s => s.Signal(), Times.Once, "every row enrolled in one transaction is covered by one wake");
        transactionActiveAtSignal.Should().BeFalse("the processor must be woken only once the rows are durable");
        (await _dbContext.Set<InternalCommandMessage>().AsNoTracking().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_CommandEnrolledThenFailure_DoesNotSignal()
    {
        var signal = new Mock<IInternalCommandSignal>();
        var scheduler = CreateScheduler(signal.Object);

        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                await scheduler.ScheduleAsync(new RecordingCommand("doomed"), runAt: null, ct);
                return Result.Failure(Error.Validation("Invariant.Failed", "a later invariant failed"));
            });

        result.IsFailure.Should().BeTrue();
        signal.Verify(s => s.Signal(), Times.Never, "a rolled-back row is not there to run");
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_CommandEnrolledThenThrow_DoesNotSignalAndANextCommitStartsClean()
    {
        var signal = new Mock<IInternalCommandSignal>();
        var scheduler = CreateScheduler(signal.Object);

        var act = async () => await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                await scheduler.ScheduleAsync(new RecordingCommand("doomed"), runAt: null, ct);
                throw new InvalidOperationException("handler blew up");
            });

        await act.Should().ThrowAsync<InvalidOperationException>();
        signal.Verify(s => s.Signal(), Times.Never, "a rolled-back row is not there to run");

        // The rolled-back enrollment must not leak into the next unit on the same context.
        var next = await _sut.ExecuteInTransactionAsync<Result>(
            async ct =>
            {
                var context = _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                context.Set<TestAggregate>().Add(new TestAggregate { Id = 1, Name = "Test" });
                await _sut.SaveChangesAsync(ct);
                return Result.Success();
            });

        next.IsSuccess.Should().BeTrue();
        signal.Verify(s => s.Signal(), Times.Never, "a unit that enrolled nothing must not wake the processor");
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_NestedEnrollment_SignalsOnceAtTheOutermostCommit()
    {
        var signal = new Mock<IInternalCommandSignal>();
        var scheduler = CreateScheduler(signal.Object);
        var signalledBeforeOuterReturned = false;

        var result = await _sut.ExecuteInTransactionAsync<Result>(
            async outerCt =>
            {
                _sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
                var inner = await _sut.ExecuteInTransactionAsync<Result>(
                    async innerCt =>
                    {
                        await scheduler.ScheduleAsync(new RecordingCommand("nested"), runAt: null, innerCt);
                        return Result.Success();
                    },
                    outerCt);
                signalledBeforeOuterReturned = signal.Invocations.Count > 0;
                return inner;
            });

        result.IsSuccess.Should().BeTrue();
        signalledBeforeOuterReturned.Should().BeFalse("an inner call does not commit, so it must not wake the processor");
        signal.Verify(s => s.Signal(), Times.Once);
    }

    private InternalCommandScheduler CreateScheduler(IInternalCommandSignal? signal = null) =>
        InternalCommandTestHarness.CreateScheduler(
            _dbContext,
            InternalCommandTestHarness.Settings(),
            new FakeTimeProvider(InternalCommandTestHarness.Epoch),
            signal ?? Mock.Of<IInternalCommandSignal>());

    // ── Test doubles ──
    public sealed record TestLocalEvent : BaseDomainEvent;

    public sealed class TestAggregate : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class TransactionTestDbContext : ApplicationDbContext
    {
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
                Mock.Of<IOutboxSignal>(), timeProvider: TimeProvider.System));
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
            modelBuilder.Entity<InternalCommandMessage>(entity =>
            {
                entity.ToTable("InternalCommands");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.CommandType).IsRequired().HasMaxLength(500);
                entity.Property(e => e.Payload).IsRequired();
                entity.Property(e => e.LastError).HasMaxLength(4000);
                entity.Property(e => e.UserRoles).HasMaxLength(512);
            });
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
