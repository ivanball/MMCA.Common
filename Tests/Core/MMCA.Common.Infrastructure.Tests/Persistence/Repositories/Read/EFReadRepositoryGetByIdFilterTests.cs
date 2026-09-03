using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Repositories;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories.Read;

/// <summary>
/// Pins the id-only <c>GetByIdAsync</c> overload against a real database with a global soft-delete
/// filter. It used to call <c>FindAsync</c>, which serves a tracked instance straight from the
/// identity map without evaluating query filters, so an entity soft-deleted earlier in the same
/// scope came back as if it were live. The replacement is a filtered query that must stay TRACKED:
/// the write repository inherits this member and the generic delete/update handlers load through
/// it, mutate, and save.
/// </summary>
public sealed class EFReadRepositoryGetByIdFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SoftDeleteTestDbContext _context;
    private readonly EFReadRepository<SoftDeletableTestEntity, int> _sut;

    public EFReadRepositoryGetByIdFilterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<SoftDeleteTestDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new SoftDeleteTestDbContext(options);
        _context.Database.EnsureCreated();
        _sut = new EFReadRepository<SoftDeletableTestEntity, int>(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetByIdAsync_EntitySoftDeletedInTheSameScope_ReturnsNull()
    {
        var entity = new SoftDeletableTestEntity { Id = 1, Name = "Doomed" };
        _context.Add(entity);
        await _context.SaveChangesAsync();

        entity.Delete().IsSuccess.Should().BeTrue();
        await _context.SaveChangesAsync();

        // The entity is still tracked, so FindAsync handed it back from the identity map and the
        // caller saw a live entity that the very same scope had just soft-deleted.
        var found = await _sut.GetByIdAsync(1);

        found.Should().BeNull("the global soft-delete filter must apply to the id-only overload too");
    }

    [Fact]
    public async Task GetByIdAsync_LiveEntity_IsReturnedAndStaysTracked()
    {
        _context.Add(new SoftDeletableTestEntity { Id = 2, Name = "Live" });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var found = await _sut.GetByIdAsync(2);

        found.Should().NotBeNull();
        found!.Name.Should().Be("Live");

        // Load-bearing: the write repository inherits this overload, and the generic delete/update
        // handlers load through it, mutate, and save. A no-tracking query would make them no-ops.
        _context.Entry(found).State.Should().Be(EntityState.Unchanged);
    }

    [Fact]
    public async Task GetByIdAsync_CalledTwice_ReturnsTheSameTrackedInstance()
    {
        _context.Add(new SoftDeletableTestEntity { Id = 3, Name = "Shared" });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var first = await _sut.GetByIdAsync(3);
        var second = await _sut.GetByIdAsync(3);

        first.Should().NotBeNull();
        ReferenceEquals(first, second).Should().BeTrue(
            "a tracked query resolves through the change tracker, so identity-map continuity survives");
    }

    /// <summary>An entity with the production soft-delete flag, mapped with the matching filter.</summary>
    public sealed class SoftDeletableTestEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class SoftDeleteTestDbContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<SoftDeletableTestEntity>(b =>
            {
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).ValueGeneratedNever();
                b.Property(e => e.Name);
                b.Ignore(e => e.RowVersion);

                // The convention-applied global filter every soft-deletable entity gets in production.
                b.HasQueryFilter(e => !e.IsDeleted);
            });
    }
}
