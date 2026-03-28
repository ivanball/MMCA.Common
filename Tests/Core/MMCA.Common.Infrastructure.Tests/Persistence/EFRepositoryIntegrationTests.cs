using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Repositories;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class EFRepositoryIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContext _context;
    private readonly EFRepository<TestEntity, int> _sut;

    public EFRepositoryIntegrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new TestDbContext(options);
        _context.Database.EnsureCreated();
        _sut = new EFRepository<TestEntity, int>(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── AddAsync + SaveChangesAsync ──
    [Fact]
    public async Task AddAsync_PersistsEntity()
    {
        var entity = TestEntity.Create(1, "Test Item");

        await _sut.AddAsync(entity);
        await _sut.SaveChangesAsync();

        var found = await _sut.GetByIdAsync(1);
        found.Should().NotBeNull();
        found!.Name.Should().Be("Test Item");
    }

    [Fact]
    public async Task AddAsync_NullEntity_Throws()
    {
        var act = () => _sut.AddAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── UpdateAsync ──
    [Fact]
    public async Task UpdateAsync_ModifiesTrackedEntity()
    {
        var entity = TestEntity.Create(1, "Original");
        await _sut.AddAsync(entity);
        await _sut.SaveChangesAsync();

        entity.Name = "Updated";
        await _sut.UpdateAsync(entity);
        await _sut.SaveChangesAsync();

        var found = await _sut.GetByIdAsync(1);
        found!.Name.Should().Be("Updated");
    }

    [Fact]
    public async Task UpdateAsync_NullEntity_Throws()
    {
        var act = () => _sut.UpdateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UpdateAsync_UntrackedEntity_AttachesAndUpdates()
    {
        var entity = TestEntity.Create(1, "Original");
        await _sut.AddAsync(entity);
        await _sut.SaveChangesAsync();

        _context.ChangeTracker.Clear();

        var detached = TestEntity.Create(1, "Detached Update");
        await _sut.UpdateAsync(detached);
        await _sut.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var found = await _sut.GetByIdAsync(1);
        found!.Name.Should().Be("Detached Update");
    }

    // ── GetByIdAsync ──
    [Fact]
    public async Task GetByIdAsync_ExistingEntity_ReturnsEntity()
    {
        var entity = TestEntity.Create(1, "Item");
        await _sut.AddAsync(entity);
        await _sut.SaveChangesAsync();

        var result = await _sut.GetByIdAsync(1);

        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistent_ReturnsNull()
    {
        var result = await _sut.GetByIdAsync(999);
        result.Should().BeNull();
    }

    // ── GetAllAsync ──
    [Fact]
    public async Task GetAllAsync_ReturnsAllEntities()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.AddAsync(TestEntity.Create(2, "B"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetAllAsync([]);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllAsync_WithWhere_FiltersCorrectly()
    {
        await _sut.AddAsync(TestEntity.Create(1, "Alpha"));
        await _sut.AddAsync(TestEntity.Create(2, "Beta"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetAllAsync([], where: e => e.Name == "Alpha");

        result.Should().HaveCount(1);
        result.First().Name.Should().Be("Alpha");
    }

    [Fact]
    public async Task GetAllAsync_WithOrderBy_SortsCorrectly()
    {
        await _sut.AddAsync(TestEntity.Create(1, "Zebra"));
        await _sut.AddAsync(TestEntity.Create(2, "Apple"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetAllAsync([], orderBy: e => e.Name);

        result.Should().HaveCount(2);
        result.First().Name.Should().Be("Apple");
        result.Last().Name.Should().Be("Zebra");
    }

    [Fact]
    public async Task GetAllAsync_WithSelect_ProjectsCorrectly()
    {
        await _sut.AddAsync(TestEntity.Create(1, "Selected"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetAllAsync(
            [],
            select: e => new TestEntity { Id = e.Id, Name = e.Name });

        result.Should().HaveCount(1);
        result.First().Name.Should().Be("Selected");
    }

    [Fact]
    public async Task GetAllAsync_WithAsTracking_ReturnsTrackedEntities()
    {
        await _sut.AddAsync(TestEntity.Create(1, "Tracked"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetAllAsync([], asTracking: true);

        result.Should().HaveCount(1);
        var entry = _context.Entry(result.First());
        entry.State.Should().Be(EntityState.Unchanged);
    }

    // ── GetAllForLookupAsync ──
    [Fact]
    public async Task GetAllForLookupAsync_ReturnsLookupProjection()
    {
        await _sut.AddAsync(TestEntity.Create(1, "LookupItem"));
        await _sut.AddAsync(TestEntity.Create(2, "AnotherItem"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetAllForLookupAsync("Name");

        result.Should().HaveCount(2);
        result.Should().Contain(l => l.Name == "LookupItem");
        result.Should().Contain(l => l.Name == "AnotherItem");
    }

    [Fact]
    public async Task GetAllForLookupAsync_WithWhere_FiltersCorrectly()
    {
        await _sut.AddAsync(TestEntity.Create(1, "Keep"));
        await _sut.AddAsync(TestEntity.Create(2, "Discard"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetAllForLookupAsync(
            "Name",
            where: e => e.Name == "Keep");

        result.Should().HaveCount(1);
        result.First().Name.Should().Be("Keep");
    }

    [Fact]
    public async Task GetAllForLookupAsync_OrdersByName()
    {
        await _sut.AddAsync(TestEntity.Create(1, "Zebra"));
        await _sut.AddAsync(TestEntity.Create(2, "Apple"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetAllForLookupAsync("Name");

        result.First().Name.Should().Be("Apple");
        result.Last().Name.Should().Be("Zebra");
    }

    [Fact]
    public async Task GetAllForLookupAsync_WithNonStringProperty_UsesToString()
    {
        await _sut.AddAsync(TestEntity.Create(1, "Item"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetAllForLookupAsync("Id");

        result.Should().HaveCount(1);
        result.First().Name.Should().Be("1");
    }

    // ── CountAsync ──
    [Fact]
    public async Task CountAsync_ReturnsCorrectCount()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.AddAsync(TestEntity.Create(2, "B"));
        await _sut.SaveChangesAsync();

        var count = await _sut.CountAsync();

        count.Should().Be(2);
    }

    [Fact]
    public async Task CountAsync_WithPredicate_FiltersCorrectly()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.AddAsync(TestEntity.Create(2, "B"));
        await _sut.SaveChangesAsync();

        var count = await _sut.CountAsync(e => e.Name == "A");

        count.Should().Be(1);
    }

    [Fact]
    public async Task CountAsync_WithNullPredicate_Throws()
    {
        var act = () => _sut.CountAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── ExistsAsync ──
    [Fact]
    public async Task ExistsAsync_ExistingEntity_ReturnsTrue()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.SaveChangesAsync();

        var exists = await _sut.ExistsAsync(1);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_NonExistent_ReturnsFalse()
    {
        var exists = await _sut.ExistsAsync(999);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_WithPredicate_Works()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.SaveChangesAsync();

        var exists = await _sut.ExistsAsync(e => e.Name == "A");

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_WithPredicate_NonExistent_ReturnsFalse()
    {
        var exists = await _sut.ExistsAsync(e => e.Name == "DoesNotExist");
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_WithNullPredicate_Throws()
    {
        var act = () => _sut.ExistsAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExistsAsync_ById_WithIgnoreQueryFilters_ReturnsTrue()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.SaveChangesAsync();

        var exists = await _sut.ExistsAsync(1, ignoreQueryFilters: true);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_ById_WithIgnoreQueryFilters_NonExistent_ReturnsFalse()
    {
        var exists = await _sut.ExistsAsync(999, ignoreQueryFilters: true);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_WithPredicate_IgnoreQueryFilters_Works()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.SaveChangesAsync();

        var exists = await _sut.ExistsAsync(e => e.Name == "A", ignoreQueryFilters: true);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_WithPredicate_IgnoreQueryFilters_NonExistent_ReturnsFalse()
    {
        var exists = await _sut.ExistsAsync(e => e.Name == "Missing", ignoreQueryFilters: true);
        exists.Should().BeFalse();
    }

    // ── Save (sync) ──
    [Fact]
    public async Task Save_PersistsChanges()
    {
        await _sut.AddAsync(TestEntity.Create(1, "Sync"));
        var count = _sut.Save();

        count.Should().BeGreaterThan(0);
        var found = await _sut.GetByIdAsync(1);
        found.Should().NotBeNull();
    }

    // ── Table properties ──
    [Fact]
    public void Table_ReturnsQueryable()
    {
        _sut.Table.Should().NotBeNull();
        _sut.TableNoTracking.Should().NotBeNull();
        _sut.TableNoTrackingSingleQuery.Should().NotBeNull();
        _sut.TableNoTrackingSplitQuery.Should().NotBeNull();
    }

    // ── ApplyIncludes ──
    [Fact]
    public async Task GetAllAsync_WithEmptyIncludes_DoesNotThrow()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetAllAsync([string.Empty, "  "]);

        result.Should().HaveCount(1);
    }

    // ── Constructor null guard ──
    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new EFReadRepository<TestEntity, int>(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── SaveChangesAsync returns count ──
    [Fact]
    public async Task SaveChangesAsync_ReturnsAffectedCount()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.AddAsync(TestEntity.Create(2, "B"));

        var count = await _sut.SaveChangesAsync();

        count.Should().Be(2);
    }

    // ── GetFullErrorTextAndRollbackEntityChanges (via constraint violation) ──
    [Fact]
    public async Task UpdateAsync_TrackedEntity_WithConflictingValues_CompletesWithoutException()
    {
        // Arrange: add two entities
        var entity1 = TestEntity.Create(1, "First");
        var entity2 = TestEntity.Create(2, "Second");
        await _sut.AddAsync(entity1);
        await _sut.AddAsync(entity2);
        await _sut.SaveChangesAsync();

        // Act: update tracked entity with same values as another entity (no unique constraint, so this succeeds)
        var updatedEntity = TestEntity.Create(1, "Second");
        await _sut.UpdateAsync(updatedEntity);

        // Assert: the update completed
        var found = await _sut.GetByIdAsync(1);
        found!.Name.Should().Be("Second");
    }

    // ── Test entity & context ──
    public sealed class TestEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public static TestEntity Create(int id, string name) =>
            new() { Id = id, Name = name };
    }

    public sealed class TestDbContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<TestEntity> TestEntities => Set<TestEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<TestEntity>(b =>
            {
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).ValueGeneratedNever();
                b.Property(e => e.Name);
            });
    }
}
