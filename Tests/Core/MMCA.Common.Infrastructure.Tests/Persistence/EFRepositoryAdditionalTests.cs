using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Repositories;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class EFRepositoryAdditionalTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContext _context;
    private readonly EFRepository<TestEntity, int> _sut;

    public EFRepositoryAdditionalTests()
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

    // ── AddRangeAsync ──
    [Fact]
    public async Task AddRangeAsync_PersistsMultipleEntities()
    {
        var entities = new[]
        {
            TestEntity.Create(1, "First"),
            TestEntity.Create(2, "Second"),
            TestEntity.Create(3, "Third")
        };

        await _sut.AddRangeAsync(entities);
        await _sut.SaveChangesAsync();

        var count = await _sut.CountAsync();
        count.Should().Be(3);
    }

    [Fact]
    public async Task AddRangeAsync_NullEntities_Throws()
    {
        var act = () => _sut.AddRangeAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── UpdateRange ──
    [Fact]
    public async Task UpdateRange_UpdatesMultipleEntities()
    {
        var entity1 = TestEntity.Create(1, "Original1");
        var entity2 = TestEntity.Create(2, "Original2");
        await _sut.AddAsync(entity1);
        await _sut.AddAsync(entity2);
        await _sut.SaveChangesAsync();

        entity1.Name = "Updated1";
        entity2.Name = "Updated2";
        _sut.UpdateRange([entity1, entity2]);
        await _sut.SaveChangesAsync();

        var found1 = await _sut.GetByIdAsync(1);
        var found2 = await _sut.GetByIdAsync(2);
        found1!.Name.Should().Be("Updated1");
        found2!.Name.Should().Be("Updated2");
    }

    [Fact]
    public void UpdateRange_NullEntities_Throws()
    {
        var act = () => _sut.UpdateRange(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── ExecuteDeleteAsync ──
    [Fact]
    public async Task ExecuteDeleteAsync_DeletesMatchingEntities()
    {
        await _sut.AddAsync(TestEntity.Create(1, "Keep"));
        await _sut.AddAsync(TestEntity.Create(2, "Delete"));
        await _sut.AddAsync(TestEntity.Create(3, "Delete"));
        await _sut.SaveChangesAsync();

        var deletedCount = await _sut.ExecuteDeleteAsync(e => e.Name == "Delete");

        deletedCount.Should().Be(2);
        var remaining = await _sut.CountAsync();
        remaining.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_NullPredicate_Throws()
    {
        var act = () => _sut.ExecuteDeleteAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── GetByIdsAsync ��─
    [Fact]
    public async Task GetByIdsAsync_ReturnsMatchingEntities()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.AddAsync(TestEntity.Create(2, "B"));
        await _sut.AddAsync(TestEntity.Create(3, "C"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetByIdsAsync([1, 3]);

        result.Should().HaveCount(2);
        result.Should().Contain(e => e.Id == 1);
        result.Should().Contain(e => e.Id == 3);
    }

    [Fact]
    public async Task GetByIdsAsync_EmptyIds_ReturnsEmptyCollection()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetByIdsAsync([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByIdsAsync_NullIds_Throws()
    {
        var act = () => _sut.GetByIdsAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ��─ GetProjectedAsync ──
    [Fact]
    public async Task GetProjectedAsync_ProjectsCorrectly()
    {
        await _sut.AddAsync(TestEntity.Create(1, "Projected"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetProjectedAsync(
            select: e => e.Name,
            where: e => e.Id == 1);

        result.Should().ContainSingle()
            .Which.Should().Be("Projected");
    }

    [Fact]
    public async Task GetProjectedAsync_NullSelect_Throws()
    {
        var act = () => _sut.GetProjectedAsync<string>(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetProjectedAsync_WithoutWhere_ReturnsAll()
    {
        await _sut.AddAsync(TestEntity.Create(1, "A"));
        await _sut.AddAsync(TestEntity.Create(2, "B"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetProjectedAsync(select: e => e.Name);

        result.Should().HaveCount(2);
    }

    // ── GetByIdAsync with includes ──
    [Fact]
    public async Task GetByIdAsync_WithIncludes_ReturnsEntity()
    {
        await _sut.AddAsync(TestEntity.Create(1, "WithIncludes"));
        await _sut.SaveChangesAsync();

        // No actual navigations in TestEntity, but verifies the code path
        var result = await _sut.GetByIdAsync(1, []);

        result.Should().NotBeNull();
        result!.Name.Should().Be("WithIncludes");
    }

    [Fact]
    public async Task GetByIdAsync_WithIncludes_NonExistent_ReturnsNull()
    {
        var result = await _sut.GetByIdAsync(999, []);
        result.Should().BeNull();
    }

    // ── GetByIdAsync null guards ��─
    [Fact]
    public async Task GetByIdAsync_NullIncludes_Throws()
    {
        var act = () => _sut.GetByIdAsync(1, null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── GetAllAsync with ignoreQueryFilters ──
    [Fact]
    public async Task GetAllAsync_WithIgnoreQueryFilters_DoesNotThrow()
    {
        await _sut.AddAsync(TestEntity.Create(1, "Filtered"));
        await _sut.SaveChangesAsync();

        var result = await _sut.GetAllAsync([], ignoreQueryFilters: true);

        result.Should().HaveCount(1);
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
