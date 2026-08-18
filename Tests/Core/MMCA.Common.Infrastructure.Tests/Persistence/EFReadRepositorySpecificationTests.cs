using System.Linq.Expressions;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Infrastructure.Persistence.Repositories;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Covers the specification-driven repository members against a real provider: the list and
/// projected-list reads, the aggregate reads, and the two pieces of state only the repository can
/// apply, tracking and soft-delete scope.
/// </summary>
public sealed class EFReadRepositorySpecificationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SpecificationTestDbContext _context;
    private readonly EFReadRepository<SpecTestEntity, int> _sut;

    public EFReadRepositorySpecificationTests()
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
        var deleted = new SpecTestEntity { Id = 5, Name = "deleted", Rank = 9 };

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

    // ── ListAsync ──
    [Fact]
    public async Task ListAsync_AppliesCriteriaOrderingAndPaging()
    {
        var rows = await _sut.ListAsync(new TopTwoByRankSpecification());

        rows.Select(e => e.Id).Should().Equal(4, 2);
    }

    [Fact]
    public async Task ListAsync_AppliesIncludes()
    {
        var rows = await _sut.ListAsync(new IncludingSpecification());

        rows.Should().ContainSingle();
        rows.Single().Children.Should().ContainSingle().Which.Label.Should().Be("one");
    }

    [Fact]
    public async Task ListAsync_IsUntrackedByDefault()
    {
        await _sut.ListAsync(new AllSpecification());

        _context.ChangeTracker.Entries().Should().BeEmpty("a specification read is a read");
    }

    [Fact]
    public async Task ListAsync_WithTracking_TracksTheResults()
    {
        await _sut.ListAsync(new TrackedSpecification());

        _context.ChangeTracker.Entries().Should().NotBeEmpty();
    }

    [Fact]
    public async Task ListAsync_ExcludesSoftDeletedRowsByDefault()
    {
        var rows = await _sut.ListAsync(new AllSpecification());

        rows.Select(e => e.Id).Should().NotContain(5);
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task ListAsync_WithSoftDeleted_IncludesThem()
    {
        var rows = await _sut.ListAsync(new IncludingSoftDeletedSpecification());

        rows.Select(e => e.Id).Should().Contain(5);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task ListAsync_WithNullSpecification_Throws()
    {
        var act = () => _sut.ListAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── ListAsync with projection ──
    [Fact]
    public async Task ListAsync_WithSelect_ProjectsServerSide()
    {
        var names = await _sut.ListAsync(new TopTwoByRankSpecification(), e => e.Name);

        names.Should().Equal("gamma", "alpha");
    }

    [Fact]
    public async Task ListAsync_WithSelect_PagesEntityRowsBeforeProjecting()
    {
        var ranks = await _sut.ListAsync(new TopTwoByRankSpecification(), e => e.Rank);

        ranks.Should().Equal([5, 3], "ordering and paging must run over the entity rows, then project");
    }

    [Fact]
    public async Task ListAsync_WithNullSelect_Throws()
    {
        var act = () => _sut.ListAsync<string>(new AllSpecification(), null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── CountAsync / AnyAsync ──
    [Fact]
    public async Task CountAsync_CountsEveryMatchingRow_IgnoringPaging()
    {
        var count = await _sut.CountAsync(new TopTwoByRankSpecification());

        count.Should().Be(4, "a count of one page of the matches is never what a caller means");
    }

    [Fact]
    public async Task CountAsync_HonorsTheCriteria()
    {
        var count = await _sut.CountAsync(new BetaSpecification());

        count.Should().Be(2);
    }

    [Fact]
    public async Task CountAsync_WithSoftDeleted_CountsThem()
    {
        (await _sut.CountAsync(new AllSpecification())).Should().Be(4);
        (await _sut.CountAsync(new IncludingSoftDeletedSpecification())).Should().Be(5);
    }

    [Fact]
    public async Task AnyAsync_ReturnsTrueWhenAnyRowMatches() =>
        (await _sut.AnyAsync(new BetaSpecification())).Should().BeTrue();

    [Fact]
    public async Task AnyAsync_ReturnsFalseWhenNoRowMatches() =>
        (await _sut.AnyAsync(new NoMatchSpecification())).Should().BeFalse();

    [Fact]
    public async Task AnyAsync_DoesNotSeeASoftDeletedRowByDefault() =>
        (await _sut.AnyAsync(new DeletedByNameSpecification())).Should().BeFalse();

    [Fact]
    public async Task CountAsync_WithNullSpecification_Throws()
    {
        var act = () => _sut.CountAsync((ISpecification<SpecTestEntity, int>)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AnyAsync_WithNullSpecification_Throws()
    {
        var act = () => _sut.AnyAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Composed specifications reach the database ──
    [Fact]
    public async Task ListAsync_WithAComposedSpecification_Translates()
    {
        var composed = new BetaSpecification().And(new HighRankSpecification().Not());

        var rows = await _sut.ListAsync(composed);

        rows.Select(e => e.Id).Should().BeEquivalentTo([1, 3]);
    }

    // ── Test specifications ──
    private sealed class AllSpecification : Specification<SpecTestEntity, int>
    {
        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => true;
    }

    private sealed class BetaSpecification : Specification<SpecTestEntity, int>
    {
        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => e.Name == "beta";
    }

    private sealed class HighRankSpecification : Specification<SpecTestEntity, int>
    {
        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => e.Rank > 4;
    }

    private sealed class NoMatchSpecification : Specification<SpecTestEntity, int>
    {
        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => e.Name == "nothing";
    }

    private sealed class DeletedByNameSpecification : Specification<SpecTestEntity, int>
    {
        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => e.Name == "deleted";
    }

    private sealed class TopTwoByRankSpecification : QuerySpecification<SpecTestEntity, int>
    {
        public TopTwoByRankSpecification()
        {
            AddOrderBy(e => e.Rank, descending: true);
            ApplyPaging(skip: 0, take: 2);
        }

        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => true;
    }

    private sealed class IncludingSpecification : QuerySpecification<SpecTestEntity, int>
    {
        public IncludingSpecification() => AddInclude(nameof(SpecTestEntity.Children));

        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => e.Id == 1;
    }

    private sealed class TrackedSpecification : QuerySpecification<SpecTestEntity, int>
    {
        public TrackedSpecification() => WithTracking();

        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => true;
    }

    private sealed class IncludingSoftDeletedSpecification : QuerySpecification<SpecTestEntity, int>
    {
        public IncludingSoftDeletedSpecification() => WithSoftDeleted();

        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => true;
    }
}
