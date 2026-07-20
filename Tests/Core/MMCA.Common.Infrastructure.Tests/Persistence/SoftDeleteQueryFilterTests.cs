using AwesomeAssertions;
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
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// End-to-end coverage for <c>ApplicationDbContext.ApplySoftDeleteFilters</c> on the SQLite
/// harness: a soft-deleted entity disappears from normal queries via the global query filter
/// and reappears with <c>IgnoreQueryFilters()</c>.
/// </summary>
public sealed class SoftDeleteQueryFilterTests : IDisposable
{
    private readonly SoftDeleteTestDbContext _dbContext = SoftDeleteTestDbContext.Create();

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task SoftDeletedEntity_DisappearsFromQueries_AndReappearsWithIgnoreQueryFilters()
    {
        var entity = new SoftDeletableEntity { Id = 1, Name = "ToDelete" };
        _dbContext.Entities.Add(entity);
        await _dbContext.SaveChangesAsync();

        entity.Delete().IsSuccess.Should().BeTrue();
        await _dbContext.SaveChangesAsync();

        (await _dbContext.Entities.AsNoTracking().ToListAsync())
            .Should().BeEmpty("the global SoftDelete filter must hide deleted rows from normal queries");

        var all = await _dbContext.Entities.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        all.Should().ContainSingle("the row is soft-deleted, not physically removed")
            .Which.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task SoftDeletedEntity_DoesNotHideOtherRows()
    {
        var deleted = new SoftDeletableEntity { Id = 1, Name = "Deleted" };
        var kept = new SoftDeletableEntity { Id = 2, Name = "Kept" };
        _dbContext.Entities.AddRange(deleted, kept);
        await _dbContext.SaveChangesAsync();

        deleted.Delete().IsSuccess.Should().BeTrue();
        await _dbContext.SaveChangesAsync();

        var visible = await _dbContext.Entities.AsNoTracking().ToListAsync();
        visible.Should().ContainSingle().Which.Id.Should().Be(2);
    }

    // ── Test doubles ──
    public sealed class SoftDeletableEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class SoftDeleteTestDbContext : ApplicationDbContext
    {
        public DbSet<SoftDeletableEntity> Entities => Set<SoftDeletableEntity>();

        internal override bool SupportsOutbox => true;

        private SoftDeleteTestDbContext(DbContextOptions<SoftDeleteTestDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static SoftDeleteTestDbContext Create()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<SoftDeleteTestDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new SoftDeleteTestDbContext(options, sp);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SoftDeletableEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Name);
                e.Property(x => x.RowVersion).IsConcurrencyToken();
            });

            // The behavior under test: the base class's global soft-delete filter.
            ApplySoftDeleteFilters(modelBuilder);
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
