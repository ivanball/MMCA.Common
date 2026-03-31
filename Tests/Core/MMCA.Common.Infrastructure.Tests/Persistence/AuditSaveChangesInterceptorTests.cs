using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class AuditSaveChangesInterceptorTests : IDisposable
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly AuditSaveChangesInterceptor _sut;
    private readonly TestAuditDbContext _dbContext;

    public AuditSaveChangesInterceptorTests()
    {
        _sut = new AuditSaveChangesInterceptor(_timeProvider);
        _dbContext = TestAuditDbContext.Create(_sut);
    }

    public void Dispose() => _dbContext.Dispose();

    // ── Added entries ──
    [Fact]
    public async Task SavingChangesAsync_AddedEntry_StampsCreatedOnAndCreatedBy()
    {
        var now = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(now);
        _dbContext.SetCurrentSaveUserId(42);

        var entity = new TestAuditEntity { Id = 1 };
        _dbContext.TestEntities.Add(entity);

        await _dbContext.SaveChangesAsync();

        entity.CreatedOn.Should().Be(now.UtcDateTime);
        entity.CreatedBy.Should().Be(42);
        entity.LastModifiedOn.Should().Be(now.UtcDateTime);
        entity.LastModifiedBy.Should().Be(42);
    }

    // ── Modified entries ──
    [Fact]
    public async Task SavingChangesAsync_ModifiedEntry_StampsLastModifiedButNotCreated()
    {
        var createTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(createTime);
        _dbContext.SetCurrentSaveUserId(10);

        var entity = new TestAuditEntity { Id = 1 };
        _dbContext.TestEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        var modifyTime = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(modifyTime);
        _dbContext.SetCurrentSaveUserId(99);

        _dbContext.Entry(entity).State = EntityState.Modified;
        await _dbContext.SaveChangesAsync();

        entity.CreatedOn.Should().Be(createTime.UtcDateTime);
        entity.CreatedBy.Should().Be(10);
        entity.LastModifiedOn.Should().Be(modifyTime.UtcDateTime);
        entity.LastModifiedBy.Should().Be(99);
    }

    // ── Deleted entries ──
    [Fact]
    public async Task SavingChangesAsync_DeletedEntry_DoesNotStampAuditFields()
    {
        var createTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(createTime);
        _dbContext.SetCurrentSaveUserId(10);

        var entity = new TestAuditEntity { Id = 1 };
        _dbContext.TestEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        DateTime originalLastModified = entity.LastModifiedOn!.Value;

        var deleteTime = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(deleteTime);
        _dbContext.SetCurrentSaveUserId(77);

        _dbContext.Entry(entity).State = EntityState.Deleted;
        await _dbContext.SaveChangesAsync();

        entity.LastModifiedOn.Should().Be(originalLastModified);
    }

    // ── Synchronous path ──
    [Fact]
    public void SavingChanges_AddedEntry_StampsAuditFields()
    {
        var now = new DateTimeOffset(2026, 5, 20, 8, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(now);
        _dbContext.SetCurrentSaveUserId(5);

        var entity = new TestAuditEntity { Id = 2 };
        _dbContext.TestEntities.Add(entity);
        _dbContext.SaveChanges();

        entity.CreatedOn.Should().Be(now.UtcDateTime);
        entity.CreatedBy.Should().Be(5);
    }

    // ── Null userId uses default ──
    [Fact]
    public async Task SavingChangesAsync_NullUserId_UsesDefaultValue()
    {
        var now = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(now);
        _dbContext.SetCurrentSaveUserId(null);

        var entity = new TestAuditEntity { Id = 3 };
        _dbContext.TestEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        entity.CreatedBy.Should().Be(default);
    }

    // ── Test helpers ──
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    public sealed class TestAuditEntity : AuditableBaseEntity<int>;

    public sealed class TestAuditDbContext : ApplicationDbContext
    {
        public DbSet<TestAuditEntity> TestEntities => Set<TestAuditEntity>();

        internal override bool SupportsOutbox => false;

        private TestAuditDbContext(DbContextOptions<TestAuditDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider())
        {
        }

        internal void SetCurrentSaveUserId(UserIdentifierType? userId) =>
            typeof(ApplicationDbContext)
                .GetProperty(nameof(CurrentSaveUserId), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(this, userId);

        public static TestAuditDbContext Create(AuditSaveChangesInterceptor interceptor)
        {
            var services = new ServiceCollection();
            services.AddSingleton(interceptor);
            services.AddSingleton<DomainEventSaveChangesInterceptor>(sp =>
            {
                var dispatcher = new Mock<Application.Interfaces.IDomainEventDispatcher>();
                var logger = new Mock<Microsoft.Extensions.Logging.ILogger<DomainEventSaveChangesInterceptor>>();
                return new DomainEventSaveChangesInterceptor(dispatcher.Object, logger.Object);
            });
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<TestAuditDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new TestAuditDbContext(options, sp);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<TestAuditEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.RowVersion).IsConcurrencyToken();
            });
    }

    private sealed class NullAssemblyProvider : Application.Interfaces.Infrastructure.IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
