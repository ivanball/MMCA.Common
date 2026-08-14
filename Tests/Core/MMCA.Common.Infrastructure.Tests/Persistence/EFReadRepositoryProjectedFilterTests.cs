using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Repositories;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Pins the <c>ignoreQueryFilters</c> flag on <c>GetProjectedAsync</c> against a real database with
/// the production NAMED soft-delete filter. Without the flag a projection could never reach a
/// soft-deleted row, so any caller needing deleted rows (an admin restore screen, a GDPR export)
/// had to abandon the projection and load whole entities.
/// </summary>
public sealed class EFReadRepositoryProjectedFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NamedSoftDeleteTestDbContext _context;
    private readonly EFReadRepository<ProjectedTestEntity, int> _sut;

    public EFReadRepositoryProjectedFilterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NamedSoftDeleteTestDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new NamedSoftDeleteTestDbContext(options);
        _context.Database.EnsureCreated();
        _sut = new EFReadRepository<ProjectedTestEntity, int>(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetProjectedAsync_WithIgnoreQueryFilters_ReturnsSoftDeletedRow()
    {
        await SeedSoftDeletedAsync();

        var result = await _sut.GetProjectedAsync(e => e.Name, ignoreQueryFilters: true);

        result.Should().ContainSingle().Which.Should().Be("Deleted");
    }

    [Fact]
    public async Task GetProjectedAsync_WithoutIgnoreQueryFilters_SkipsSoftDeletedRow()
    {
        await SeedSoftDeletedAsync();

        var result = await _sut.GetProjectedAsync(e => e.Name);

        result.Should().BeEmpty("the soft-delete filter applies unless the caller opts out");
    }

    [Fact]
    public async Task GetProjectedAsync_WithIgnoreQueryFilters_StillHonorsTheWherePredicate()
    {
        await SeedSoftDeletedAsync();
        _context.Add(new ProjectedTestEntity { Id = 2, Name = "Live" });
        await _context.SaveChangesAsync();

        var result = await _sut.GetProjectedAsync(
            select: e => e.Name,
            where: e => e.Id == 1,
            ignoreQueryFilters: true);

        result.Should().ContainSingle().Which.Should().Be("Deleted");
    }

    private async Task SeedSoftDeletedAsync()
    {
        var entity = new ProjectedTestEntity { Id = 1, Name = "Deleted" };
        _context.Add(entity);
        await _context.SaveChangesAsync();

        entity.Delete().IsSuccess.Should().BeTrue();
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>An entity with the production soft-delete flag, mapped with the matching named filter.</summary>
    public sealed class ProjectedTestEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class NamedSoftDeleteTestDbContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<ProjectedTestEntity>(b =>
            {
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).ValueGeneratedNever();
                b.Property(e => e.Name);
                b.Ignore(e => e.RowVersion);

                // The NAME matters: the repository drops this filter by name so the Tenant filter survives.
                b.HasQueryFilter(ApplicationDbContext.SoftDeleteFilterName, e => !e.IsDeleted);
            });
    }
}
