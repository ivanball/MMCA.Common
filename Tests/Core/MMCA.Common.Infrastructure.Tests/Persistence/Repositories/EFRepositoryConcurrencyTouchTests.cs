using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using TestDbContext = MMCA.Common.Infrastructure.Tests.Persistence.Repositories.EFRepositoryAdditionalTests.TestDbContext;
using TestEntity = MMCA.Common.Infrastructure.Tests.Persistence.Repositories.EFRepositoryAdditionalTests.TestEntity;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// SEC-Common-77: stamping the caller's token as the ORIGINAL value does not make the root entry
/// dirty, so an applier that touched only child rows left the root Unchanged and EF emitted no root
/// UPDATE at all: no concurrency predicate, no 412, a silent lost update.
/// <see cref="EFRepository{TEntity, TIdentifierType}.TouchConcurrencyToken"/> is what puts the root
/// back in the statement.
/// </summary>
public sealed class EFRepositoryConcurrencyTouchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContext _context;
    private readonly EFRepository<TestEntity, int> _sut;

    public EFRepositoryConcurrencyTouchTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new TestDbContext(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _sut = new EFRepository<TestEntity, int>(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<TestEntity> SeedAsync()
    {
        var entity = TestEntity.Create(1, "seed");
        await _sut.AddAsync(entity, TestContext.Current.CancellationToken);
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);
        _context.ChangeTracker.Clear();

        return await _context.Set<TestEntity>().SingleAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TouchConcurrencyToken_MakesAnUnchangedRootParticipateInTheSave()
    {
        var entity = await SeedAsync();

        _context.Entry(entity).State.Should().Be(EntityState.Unchanged,
            "this is the child-only edit the finding describes: nothing on the root changed");

        _sut.TouchConcurrencyToken(entity);

        _context.Entry(entity).State.Should().Be(EntityState.Modified);
        _context.Entry(entity).Property(nameof(TestEntity.LastModifiedOn)).IsModified.Should().BeTrue();
    }

    [Fact]
    public async Task TouchConcurrencyToken_LeavesAnAlreadyModifiedRootAlone()
    {
        var entity = await SeedAsync();
        entity.Name = "renamed";

        _context.Entry(entity).State.Should().Be(EntityState.Modified);

        _sut.TouchConcurrencyToken(entity);

        // Already emitting an UPDATE, which already carries the concurrency predicate.
        _context.Entry(entity).State.Should().Be(EntityState.Modified);
        _context.Entry(entity).Property(nameof(TestEntity.Name)).IsModified.Should().BeTrue();
    }

    [Fact]
    public async Task TouchConcurrencyToken_LeavesADeletedRootAlone()
    {
        var entity = await SeedAsync();
        _context.Remove(entity);

        _sut.TouchConcurrencyToken(entity);

        _context.Entry(entity).State.Should().Be(EntityState.Deleted);
    }

    [Fact]
    public void TouchConcurrencyToken_RejectsNull()
    {
        Action touch = () => _sut.TouchConcurrencyToken(null!);

        touch.Should().Throw<ArgumentNullException>();
    }
}
