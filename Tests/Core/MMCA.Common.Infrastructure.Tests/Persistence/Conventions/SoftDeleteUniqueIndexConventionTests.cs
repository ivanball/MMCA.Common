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

namespace MMCA.Common.Infrastructure.Tests.Persistence.Conventions;

/// <summary>
/// Tests for <c>SoftDeleteUniqueIndexConvention</c> (registered by
/// <c>ApplicationDbContext.ConfigureConventions</c>) on the SQLite harness: unique indexes on
/// soft-deletable entities get an <c>IsDeleted = 0</c> filter, so a soft-deleted row no longer
/// occupies its unique slot, while uniqueness among live rows stays enforced.
/// </summary>
public sealed class SoftDeleteUniqueIndexConventionTests : IDisposable
{
    private readonly UniqueIndexTestDbContext _dbContext = UniqueIndexTestDbContext.Create();

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public void UniqueIndexOnSoftDeletableEntity_GetsIsDeletedFilter()
    {
        var index = _dbContext.Model.FindEntityType(typeof(UniqueNamedEntity))!
            .GetIndexes()
            .Single(i => i.IsUnique);

        index.GetFilter().Should().Be("\"IsDeleted\" = 0");
    }

    [Fact]
    public async Task SoftDeletedRow_NoLongerBlocksInsertingSameUniqueValue()
    {
        var original = new UniqueNamedEntity { Id = 1, Name = "X" };
        _dbContext.Entities.Add(original);
        await _dbContext.SaveChangesAsync();

        original.Delete().IsSuccess.Should().BeTrue();
        await _dbContext.SaveChangesAsync();

        var replacement = new UniqueNamedEntity { Id = 2, Name = "X" };
        _dbContext.Entities.Add(replacement);
        var act = async () => await _dbContext.SaveChangesAsync();

        await act.Should().NotThrowAsync(
            "the filtered unique index must ignore the soft-deleted row, so re-creating the \"same\" record succeeds");

        // This harness applies no soft-delete query filter, so both rows are visible: the
        // deleted original and the live replacement coexist under the same unique value.
        var rows = await _dbContext.Entities.AsNoTracking().Where(e => e.Name == "X").ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(e => !e.IsDeleted).Id.Should().Be(2);
    }

    [Fact]
    public async Task LiveDuplicate_IsStillRejectedByTheUniqueIndex()
    {
        _dbContext.Entities.Add(new UniqueNamedEntity { Id = 1, Name = "X" });
        await _dbContext.SaveChangesAsync();

        _dbContext.Entities.Add(new UniqueNamedEntity { Id = 2, Name = "X" });
        var act = async () => await _dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>(
            "the filter must not weaken uniqueness among non-deleted rows");
    }

    [Fact]
    public void HandAuthoredIndexFilter_IsLeftUntouched()
    {
        var index = _dbContext.Model.FindEntityType(typeof(FilteredIndexEntity))!
            .GetIndexes()
            .Single(i => i.IsUnique);

        index.GetFilter().Should().Be("\"Name\" <> ''", "hand-authored filters win over the convention");
    }

    // ── Test doubles ──
    public sealed class UniqueNamedEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class FilteredIndexEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class UniqueIndexTestDbContext : ApplicationDbContext
    {
        public DbSet<UniqueNamedEntity> Entities => Set<UniqueNamedEntity>();

        internal override bool SupportsOutbox => true;

        private UniqueIndexTestDbContext(DbContextOptions<UniqueIndexTestDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static UniqueIndexTestDbContext Create()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<UniqueIndexTestDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new UniqueIndexTestDbContext(options, sp);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UniqueNamedEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Name);
                e.Property(x => x.RowVersion).IsConcurrencyToken();
                e.HasIndex(x => x.Name).IsUnique();
            });
            modelBuilder.Entity<FilteredIndexEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Name);
                e.Property(x => x.RowVersion).IsConcurrencyToken();
                e.HasIndex(x => x.Name).IsUnique().HasFilter("\"Name\" <> ''");
            });
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
