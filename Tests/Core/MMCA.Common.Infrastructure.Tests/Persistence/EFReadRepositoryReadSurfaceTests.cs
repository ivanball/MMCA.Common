using System.Linq.Expressions;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Infrastructure.Persistence.Repositories;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Covers the reads that exist so a handler stops folding results in memory: the TOP-1 predicate
/// read, the two grouped aggregates, and the active-versus-soft-deleted split. All of them are
/// exercised against a real provider, because the whole point of the members is what the DATABASE
/// does with them.
/// </summary>
public sealed class EFReadRepositoryReadSurfaceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SpecificationTestDbContext _context;
    private readonly EFReadRepository<SpecTestEntity, int> _sut;

    public EFReadRepositoryReadSurfaceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SpecificationTestDbContext(
            new DbContextOptionsBuilder<SpecificationTestDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _sut = new EFReadRepository<SpecTestEntity, int>(_context);

        Seed();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private void Seed()
    {
        var deleted = new SpecTestEntity { Id = 5, Name = "beta", Rank = 9, Category = "x" };

        _context.AddRange(
            new SpecTestEntity { Id = 1, Name = "beta", Rank = 2, Category = "x" },
            new SpecTestEntity { Id = 2, Name = "alpha", Rank = 3 },
            new SpecTestEntity { Id = 3, Name = "beta", Rank = 1, Category = "y" },
            new SpecTestEntity { Id = 4, Name = "gamma", Rank = 5, Category = "x" },
            deleted);
        _context.Add(new SpecTestChild { Id = 10, SpecTestEntityId = 1, Label = "one" });
        _context.SaveChanges();

        deleted.Delete().IsSuccess.Should().BeTrue();
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    // ── FirstOrDefaultAsync (predicate) ──
    [Fact]
    public async Task FirstOrDefaultAsync_MatchingPredicate_ReturnsOneEntity()
    {
        var found = await _sut.FirstOrDefaultAsync(e => e.Name == "gamma");

        found.Should().NotBeNull();
        found!.Id.Should().Be(4);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_NoMatch_ReturnsNull() =>
        (await _sut.FirstOrDefaultAsync(e => e.Name == "nothing")).Should().BeNull();

    [Fact]
    public async Task FirstOrDefaultAsync_SoftDeletedMatch_IsExcludedByDefault() =>
        (await _sut.FirstOrDefaultAsync(e => e.Id == 5)).Should().BeNull();

    [Fact]
    public async Task FirstOrDefaultAsync_WithIgnoreQueryFilters_SeesTheSoftDeletedRow()
    {
        var found = await _sut.FirstOrDefaultAsync(e => e.Id == 5, ignoreQueryFilters: true);

        found.Should().NotBeNull();
        found!.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task FirstOrDefaultAsync_WithIncludes_EagerLoadsTheNavigation()
    {
        var found = await _sut.FirstOrDefaultAsync(e => e.Id == 1, includes: ["Children"]);

        found.Should().NotBeNull();
        found!.Children.Should().ContainSingle().Which.Label.Should().Be("one");
    }

    [Fact]
    public async Task FirstOrDefaultAsync_WithoutIncludes_LeavesTheNavigationEmpty()
    {
        var found = await _sut.FirstOrDefaultAsync(e => e.Id == 1);

        found.Should().NotBeNull();
        found!.Children.Should().BeEmpty("no include was asked for, so nothing was eager-loaded");
    }

    [Fact]
    public async Task FirstOrDefaultAsync_DefaultsToNoTracking()
    {
        var found = await _sut.FirstOrDefaultAsync(e => e.Id == 4);

        found.Should().NotBeNull();
        _context.Entry(found!).State.Should().Be(EntityState.Detached);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_WithAsTracking_TracksTheEntity()
    {
        var found = await _sut.FirstOrDefaultAsync(e => e.Id == 4, asTracking: true);

        found.Should().NotBeNull();
        _context.Entry(found!).State.Should().Be(EntityState.Unchanged);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_WithNullPredicate_ThrowsArgumentNullException()
    {
        var act = async () => await _sut.FirstOrDefaultAsync(where: null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── FirstOrDefaultAsync (specification) ──
    [Fact]
    public async Task FirstOrDefaultAsync_WithSpecification_HonorsItsOrdering()
    {
        var found = await _sut.FirstOrDefaultAsync(new LowestRankedBetaSpecification());

        found.Should().NotBeNull();
        found!.Id.Should().Be(3, "rank 1 sorts before rank 2, which is the whole reason to pass a specification");
    }

    [Fact]
    public async Task FirstOrDefaultAsync_WithSpecification_ExcludesSoftDeletedRows()
    {
        var found = await _sut.FirstOrDefaultAsync(new HighestRankedBetaSpecification());

        found.Should().NotBeNull();
        found!.Id.Should().Be(1, "the soft-deleted rank 9 beta is filtered out before the ordering picks a winner");
    }

    [Fact]
    public async Task FirstOrDefaultAsync_WithSpecification_MatchingNothing_ReturnsNull() =>
        (await _sut.FirstOrDefaultAsync(new NoMatchSpecification())).Should().BeNull();

    [Fact]
    public async Task FirstOrDefaultAsync_WithNullSpecification_ThrowsArgumentNullException()
    {
        var act = async () => await _sut.FirstOrDefaultAsync(specification: null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── CountByAsync ──
    [Fact]
    public async Task CountByAsync_GroupsInTheDatabaseAndSkipsSoftDeletedRows()
    {
        var counts = await _sut.CountByAsync(e => e.Name);

        counts.Should().HaveCount(3);
        counts["beta"].Should().Be(2, "the third beta is soft-deleted and the global filter still applies");
        counts["alpha"].Should().Be(1);
        counts["gamma"].Should().Be(1);
    }

    [Fact]
    public async Task CountByAsync_WithPredicate_GroupsOnlyTheMatchingRows()
    {
        var counts = await _sut.CountByAsync(e => e.Name, where: e => e.Rank >= 2);

        counts.Should().HaveCount(3);
        counts["beta"].Should().Be(1, "rank 1 was filtered out before the grouping");
    }

    [Fact]
    public async Task CountByAsync_WithAPredicateMatchingNothing_ReturnsAnEmptyDictionary() =>
        (await _sut.CountByAsync(e => e.Name, where: e => e.Rank > 100)).Should().BeEmpty();

    [Fact]
    public async Task CountByAsync_WithNullKeySelector_ThrowsArgumentNullException()
    {
        var act = async () => await _sut.CountByAsync<string>(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── SumByAsync ──
    [Fact]
    public async Task SumByAsync_SumsPerKeyInTheDatabaseAndSkipsSoftDeletedRows()
    {
        var totals = await _sut.SumByAsync(e => e.Name, e => e.Rank);

        totals.Should().HaveCount(3);
        totals["beta"].Should().Be(3m, "2 + 1; the soft-deleted rank 9 row is excluded");
        totals["alpha"].Should().Be(3m);
        totals["gamma"].Should().Be(5m);
    }

    [Fact]
    public async Task SumByAsync_WithPredicate_SumsOnlyTheMatchingRows()
    {
        var totals = await _sut.SumByAsync(e => e.Name, e => e.Rank, where: e => e.Category == "x");

        totals.Should().HaveCount(2);
        totals["beta"].Should().Be(2m);
        totals["gamma"].Should().Be(5m);
    }

    [Fact]
    public async Task SumByAsync_WithNullSumSelector_ThrowsArgumentNullException()
    {
        var act = async () => await _sut.SumByAsync(e => e.Name, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── FindIncludingDeletedAsync ──
    [Fact]
    public async Task FindIncludingDeletedAsync_SplitsTheMatchesIntoActiveAndSoftDeleted()
    {
        var (active, softDeleted) = await _sut.FindIncludingDeletedAsync(e => e.Name == "beta");

        active.Select(e => e.Id).Should().BeEquivalentTo([1, 3]);
        softDeleted.Select(e => e.Id).Should().BeEquivalentTo([5]);
    }

    [Fact]
    public async Task FindIncludingDeletedAsync_WithNoSoftDeletedMatch_ReturnsAnEmptySecondHalf()
    {
        var (active, softDeleted) = await _sut.FindIncludingDeletedAsync(e => e.Name == "gamma");

        active.Should().ContainSingle();
        softDeleted.Should().BeEmpty();
    }

    [Fact]
    public async Task FindIncludingDeletedAsync_WithNoMatchAtAll_ReturnsTwoEmptyHalves()
    {
        var (active, softDeleted) = await _sut.FindIncludingDeletedAsync(e => e.Name == "nothing");

        active.Should().BeEmpty();
        softDeleted.Should().BeEmpty();
    }

    [Fact]
    public async Task FindIncludingDeletedAsync_WithAsTracking_TracksTheSoftDeletedRowForReactivation()
    {
        var (_, softDeleted) = await _sut.FindIncludingDeletedAsync(e => e.Id == 5, asTracking: true);

        var candidate = softDeleted.Should().ContainSingle().Subject;
        _context.Entry(candidate).State.Should().Be(
            EntityState.Unchanged,
            "an untracked candidate would make the reactivation that follows save nothing");
    }

    [Fact]
    public async Task FindIncludingDeletedAsync_WithIncludes_EagerLoadsTheNavigation()
    {
        var (active, _) = await _sut.FindIncludingDeletedAsync(e => e.Id == 1, includes: ["Children"]);

        active.Should().ContainSingle().Which.Children.Should().ContainSingle();
    }

    [Fact]
    public async Task FindIncludingDeletedAsync_WithNullPredicate_ThrowsArgumentNullException()
    {
        var act = async () => await _sut.FindIncludingDeletedAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Test specifications ──
    private sealed class LowestRankedBetaSpecification : QuerySpecification<SpecTestEntity, int>
    {
        public LowestRankedBetaSpecification() => AddOrderBy(e => e.Rank);

        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => e.Name == "beta";
    }

    private sealed class HighestRankedBetaSpecification : QuerySpecification<SpecTestEntity, int>
    {
        public HighestRankedBetaSpecification() => AddOrderBy(e => e.Rank, descending: true);

        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => e.Name == "beta";
    }

    private sealed class NoMatchSpecification : Specification<SpecTestEntity, int>
    {
        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => e.Rank > 100;
    }
}
