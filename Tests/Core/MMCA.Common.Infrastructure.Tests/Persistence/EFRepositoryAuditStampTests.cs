using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Infrastructure.Persistence.Repositories.Factory;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// A repository stages changes and never flushes them: the unit of work does, and its flush must
/// stamp the acting user. These tests pin that end of the path, from
/// <see cref="ICurrentUserService.UserId"/> through <see cref="UnitOfWork"/> and
/// <see cref="DbContextFactory"/> into <see cref="ApplicationDbContext"/>, where the audit
/// interceptor reads it. When the id does not make it that far the interceptor falls back to the
/// system sentinel and everything written in the scope is attributed to nobody.
/// </summary>
public sealed class EFRepositoryAuditStampTests : IDisposable
{
    private const int ActingUserId = 42;

    private static readonly DataSourceKey SqliteKey = DataSourceKey.Default(DataSource.Sqlite);

    private readonly SqliteConnection _connection;
    private readonly StampTestDbContext _context;

    public EFRepositoryAuditStampTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = StampTestDbContext.Create(_connection);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task SaveChangesAsync_OnApplicationDbContext_StampsTheActingUser()
    {
        var sut = CreateUnitOfWork(CurrentUser(ActingUserId));
        await sut.GetRepository<StampedEntity, int>().AddAsync(new StampedEntity { Id = 1 });

        await sut.SaveChangesAsync();

        var stored = await _context.Set<StampedEntity>().AsNoTracking().SingleAsync(e => e.Id == 1);
        stored.CreatedBy.Should().Be(ActingUserId);
        stored.LastModifiedBy.Should().Be(ActingUserId);
    }

    [Fact]
    public async Task Save_OnApplicationDbContext_StampsTheActingUser()
    {
        var sut = CreateUnitOfWork(CurrentUser(ActingUserId));
        await sut.GetRepository<StampedEntity, int>().AddAsync(new StampedEntity { Id = 2 });

        var written = sut.Save();

        written.Should().Be(1);
        var stored = await _context.Set<StampedEntity>().AsNoTracking().SingleAsync(e => e.Id == 2);
        stored.CreatedBy.Should().Be(ActingUserId);
    }

    [Fact]
    public async Task SaveChangesAsync_WithNoCurrentUser_FallsBackToTheSystemSentinel()
    {
        var sut = CreateUnitOfWork(Mock.Of<ICurrentUserService>());
        await sut.GetRepository<StampedEntity, int>().AddAsync(new StampedEntity { Id = 3 });

        await sut.SaveChangesAsync();

        var stored = await _context.Set<StampedEntity>().AsNoTracking().SingleAsync(e => e.Id == 3);
        stored.CreatedBy.Should().Be(default, "a scope with no acting user is a system operation");
    }

    [Fact]
    public async Task SaveChangesAsync_ReturnsTheNumberOfRowsTheRepositoryStaged()
    {
        var sut = CreateUnitOfWork(CurrentUser(ActingUserId));
        var repository = sut.GetRepository<StampedEntity, int>();
        await repository.AddAsync(new StampedEntity { Id = 4 });
        await repository.AddAsync(new StampedEntity { Id = 5 });

        var written = await sut.SaveChangesAsync();

        written.Should().Be(2, "the unit of work flushes every change staged through its repositories");
    }

    /// <summary>
    /// Builds the real save path over the in-memory context: only the physical factory, the source
    /// registry, and the repository factory are doubled, so the user id travels through the same
    /// <see cref="UnitOfWork"/> and <see cref="DbContextFactory"/> code a host runs.
    /// </summary>
    private UnitOfWork CreateUnitOfWork(ICurrentUserService currentUserService)
    {
        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        physicalFactory.Setup(f => f.Create(It.IsAny<DataSourceKey>())).Returns(_context);

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        var dbContextFactory = new DbContextFactory(
            physicalFactory.Object,
            registry.Object,
            new DefaultDataSourceResolver(),
            currentUserService);

        var dataSourceService = new Mock<IDataSourceService>();
        dataSourceService.Setup(s => s.GetDataSourceKey(typeof(StampedEntity))).Returns(SqliteKey);

        var repositoryFactory = new Mock<IRepositoryFactory>();
        repositoryFactory
            .Setup(f => f.Create<StampedEntity, int>(It.IsAny<DbContext>()))
            .Returns<DbContext>(context => new EFRepository<StampedEntity, int>(context));

        return new UnitOfWork(dbContextFactory, dataSourceService.Object, repositoryFactory.Object);
    }

    private static ICurrentUserService CurrentUser(UserIdentifierType userId)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(userId);
        return currentUser.Object;
    }

    // ── Test doubles ──
    public sealed class StampedEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class StampTestDbContext : ApplicationDbContext
    {
        private StampTestDbContext(DbContextOptions<StampTestDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        internal override bool SupportsOutbox => false;

        public static StampTestDbContext Create(SqliteConnection connection)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<StampTestDbContext>()
                .UseSqlite(connection)
                .Options;

            var context = new StampTestDbContext(options, sp);
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<StampedEntity>(e =>
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
