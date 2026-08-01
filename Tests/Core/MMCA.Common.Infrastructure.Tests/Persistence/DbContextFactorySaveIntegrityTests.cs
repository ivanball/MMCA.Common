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
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// The save loop in <see cref="DbContextFactory.SaveChangesAsync"/> is bounded, and in-process
/// domain event dispatch runs inside it, so a handler can leave tracked changes behind that the
/// loop will never reach. These tests pin the post-loop assertion that turns that silent data loss
/// into a failure, and pin that a context which merely reads does not trip it.
/// </summary>
public sealed class DbContextFactorySaveIntegrityTests : IDisposable
{
    private static readonly DataSourceKey PrimaryKey = new(DataSource.Sqlite, "Primary");
    private static readonly DataSourceKey SecondaryKey = new(DataSource.Sqlite, "Secondary");

    private readonly SqliteConnection _primaryConnection;
    private readonly SqliteConnection _secondaryConnection;
    private readonly Mock<IDomainEventDispatcher> _dispatcherMock = new();
    private readonly IntegrityTestDbContext _primary;
    private readonly IntegrityTestDbContext _secondary;
    private readonly DbContextFactory _sut;

    public DbContextFactorySaveIntegrityTests()
    {
        _primaryConnection = new SqliteConnection("DataSource=:memory:");
        _primaryConnection.Open();
        _secondaryConnection = new SqliteConnection("DataSource=:memory:");
        _secondaryConnection.Open();

        _primary = IntegrityTestDbContext.Create(_primaryConnection, _dispatcherMock.Object, PrimaryKey);
        _secondary = IntegrityTestDbContext.Create(_secondaryConnection, _dispatcherMock.Object, SecondaryKey);

        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        physicalFactory
            .Setup(f => f.Create(It.IsAny<DataSourceKey>()))
            .Returns<DataSourceKey>(key => key == SecondaryKey ? _secondary : _primary);

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
        _primary.Dispose();
        _secondary.Dispose();
        _primaryConnection.Dispose();
        _secondaryConnection.Dispose();
    }

    // ── A handler mutating an ALREADY-saved context: in the saved set, yet dirty ──
    [Fact]
    public async Task SaveChangesAsync_HandlerMutatesAnAlreadySavedContext_ThrowsNamingThatContext()
    {
        _dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _primary.Set<IntegrityAggregate>().Add(new IntegrityAggregate { Id = 2, Name = "raised by a handler" }))
            .Returns(Task.CompletedTask);

        var context = _sut.GetDbContext(PrimaryKey);
        var aggregate = new IntegrityAggregate { Id = 1, Name = "root" };
        aggregate.AddDomainEvent(new IntegrityEvent());
        context.Set<IntegrityAggregate>().Add(aggregate);

        var act = async () => await _sut.SaveChangesAsync();

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.WithMessage(
            "*Sqlite/Primary*",
            "the failure must name the source whose changes would otherwise have been discarded");
    }

    // ── A context that only reads must never trip the assertion ──
    [Fact]
    public async Task SaveChangesAsync_ExtraReadOnlyContext_DoesNotThrow()
    {
        _secondary.Set<IntegrityAggregate>().Add(new IntegrityAggregate { Id = 99, Name = "pre-existing" });
        await _secondary.SaveChangesAsync(null);
        _secondary.ChangeTracker.Clear();

        var readOnly = _sut.GetDbContext(SecondaryKey);
        var tracked = await readOnly.Set<IntegrityAggregate>().ToListAsync();
        var writable = _sut.GetDbContext(PrimaryKey);
        writable.Set<IntegrityAggregate>().Add(new IntegrityAggregate { Id = 1, Name = "written" });

        var act = async () => await _sut.SaveChangesAsync();

        await act.Should().NotThrowAsync();
        tracked.Should().ContainSingle("the read-only context tracks an Unchanged row, which is not a pending change");
    }

    // ── Baseline: an ordinary save with no handler side effects stays clean ──
    [Fact]
    public async Task SaveChangesAsync_NothingLeftTracked_ReturnsTheWrittenCount()
    {
        var context = _sut.GetDbContext(PrimaryKey);
        context.Set<IntegrityAggregate>().Add(new IntegrityAggregate { Id = 1, Name = "root" });

        var written = await _sut.SaveChangesAsync();

        written.Should().Be(1);
    }

    // ── Test doubles ──
    public sealed record IntegrityEvent : BaseDomainEvent;

    public sealed class IntegrityAggregate : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class IntegrityTestDbContext : ApplicationDbContext
    {
        private IntegrityTestDbContext(
            DbContextOptions<IntegrityTestDbContext> options,
            IServiceProvider serviceProvider,
            PhysicalDataSource physicalDataSource)
            : base(options, serviceProvider, new NullAssemblyProvider(), physicalDataSource)
        {
        }

        // No outbox: this fixture is about the save loop, not about event routing, and the local
        // events then dispatch in-process on every save (which is what materializes the defect).
        internal override bool SupportsOutbox => false;

        public static IntegrityTestDbContext Create(
            SqliteConnection connection,
            IDomainEventDispatcher dispatcher,
            DataSourceKey key)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                dispatcher,
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<IntegrityTestDbContext>()
                .UseSqlite(connection)
                .Options;

            var context = new IntegrityTestDbContext(
                options,
                sp,
                new PhysicalDataSource(key, connection.ConnectionString, null, "AtlDevCon"));
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<IntegrityAggregate>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Name);
                e.Property(x => x.RowVersion).IsConcurrencyToken();
            });
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
