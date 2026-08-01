using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// The repository's own save entry points must stamp the acting user, exactly like the unit of
/// work does. They used to call the plain EF overloads, which leave
/// <c>ApplicationDbContext.CurrentSaveUserId</c> null and make the audit interceptor fall back to
/// the system sentinel, so anything written through them was attributed to nobody.
/// </summary>
public sealed class EFRepositoryAuditStampTests : IDisposable
{
    private const int ActingUserId = 42;

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
        var sut = new EFRepository<StampedEntity, int>(_context, TimeProvider.System, CurrentUser(ActingUserId));
        await sut.AddAsync(new StampedEntity { Id = 1 });

        await sut.SaveChangesAsync();

        var stored = await _context.Set<StampedEntity>().AsNoTracking().SingleAsync(e => e.Id == 1);
        stored.CreatedBy.Should().Be(ActingUserId);
        stored.LastModifiedBy.Should().Be(ActingUserId);
    }

    [Fact]
    public async Task Save_OnApplicationDbContext_StampsTheActingUser()
    {
        var sut = new EFRepository<StampedEntity, int>(_context, TimeProvider.System, CurrentUser(ActingUserId));
        await sut.AddAsync(new StampedEntity { Id = 2 });

        var written = sut.Save();

        written.Should().Be(1);
        var stored = await _context.Set<StampedEntity>().AsNoTracking().SingleAsync(e => e.Id == 2);
        stored.CreatedBy.Should().Be(ActingUserId);
    }

    [Fact]
    public async Task SaveChangesAsync_WithNoCurrentUserService_FallsBackToTheSystemSentinel()
    {
        var sut = new EFRepository<StampedEntity, int>(_context);
        await sut.AddAsync(new StampedEntity { Id = 3 });

        await sut.SaveChangesAsync();

        var stored = await _context.Set<StampedEntity>().AsNoTracking().SingleAsync(e => e.Id == 3);
        stored.CreatedBy.Should().Be(default, "a repository built without a user is a system operation");
    }

    [Fact]
    public async Task SaveChangesAsync_OnAContextThatIsNotAnApplicationDbContext_StillPersists()
    {
        await using var plainContext = PlainDbContext.Create();
        var sut = new EFRepository<StampedEntity, int>(plainContext, TimeProvider.System, CurrentUser(ActingUserId));
        await sut.AddAsync(new StampedEntity { Id = 4 });

        var written = await sut.SaveChangesAsync();

        written.Should().Be(1, "the user-id overloads only exist on ApplicationDbContext; anything else keeps the plain path");
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

    /// <summary>A bare EF context: the direct-construction path the repository must keep supporting.</summary>
    public sealed class PlainDbContext(DbContextOptions<PlainDbContext> options) : DbContext(options)
    {
        public static PlainDbContext Create()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();
            var context = new PlainDbContext(new DbContextOptionsBuilder<PlainDbContext>().UseSqlite(connection).Options);
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<StampedEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Name);
            });
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
