using System.Linq.Expressions;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Infrastructure.Persistence.Repositories;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Exercises <c>SpecificationEvaluator</c> against a real provider (SQLite in-memory), because the
/// interesting parts are all translation: an ordering chain bound back to its concrete key type by
/// reflection, includes with the collection split-query switch, and Skip/Take.
/// </summary>
public sealed class SpecificationEvaluatorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SpecificationTestDbContext _context;

    public SpecificationEvaluatorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SpecificationTestDbContext(
            new DbContextOptionsBuilder<SpecificationTestDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        Seed();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private void Seed()
    {
        _context.AddRange(
            new SpecTestEntity { Id = 1, Name = "beta", Rank = 2, Category = "x" },
            new SpecTestEntity { Id = 2, Name = "alpha", Rank = 3, Category = null },
            new SpecTestEntity { Id = 3, Name = "beta", Rank = 1, Category = "y" },
            new SpecTestEntity { Id = 4, Name = "gamma", Rank = 5, Category = "x" });

        _context.AddRange(
            new SpecTestChild { Id = 10, SpecTestEntityId = 1, Label = "one" },
            new SpecTestChild { Id = 11, SpecTestEntityId = 1, Label = "two" });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private IQueryable<SpecTestEntity> Source => _context.Entities.AsNoTracking();

    // ── Criteria ──
    [Fact]
    public async Task Apply_AlwaysAppliesTheCriteria()
    {
        var query = SpecificationEvaluator.Apply<SpecTestEntity, int>(Source, new BetaSpecification());

        var ids = await query.Select(e => e.Id).ToListAsync();

        ids.Should().BeEquivalentTo([1, 3]);
    }

    [Fact]
    public void Apply_WithNullSource_Throws()
    {
        var act = () => SpecificationEvaluator.Apply<SpecTestEntity, int>(null!, new BetaSpecification());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Apply_WithNullSpecification_Throws()
    {
        var act = () => SpecificationEvaluator.Apply<SpecTestEntity, int>(Source, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Apply_WithAPlainSpecification_AddsNoShape()
    {
        // A plain (non-query) specification contributes criteria only, so the natural order survives.
        var query = SpecificationEvaluator.Apply<SpecTestEntity, int>(Source, new BetaSpecification());

        var sql = query.ToQueryString();
        var ids = await query.Select(e => e.Id).ToListAsync();

        sql.Should().NotContain("ORDER BY");
        sql.Should().NotContain("LIMIT");
        ids.Should().HaveCount(2);
    }

    // ── Ordering ──
    [Fact]
    public async Task Apply_AppliesTheOrderingChainInOrder()
    {
        var query = SpecificationEvaluator.Apply<SpecTestEntity, int>(Source, new OrderedSpecification());

        var ids = await query.Select(e => e.Id).ToListAsync();

        // Name ascending, then Rank descending: alpha(2), beta rank 2 (1), beta rank 1 (3), gamma(4).
        ids.Should().Equal(2, 1, 3, 4);
    }

    [Fact]
    public async Task Apply_HonorsADescendingFirstKey()
    {
        var query = SpecificationEvaluator.Apply<SpecTestEntity, int>(Source, new RankDescendingSpecification());

        var ids = await query.Select(e => e.Id).ToListAsync();

        ids.Should().Equal(4, 2, 1, 3);
    }

    [Fact]
    public async Task Apply_WithNoOrdering_LeavesTheQueryUnordered()
    {
        var query = SpecificationEvaluator.Apply<SpecTestEntity, int>(Source, new UnorderedQuerySpecification());

        query.ToQueryString().Should().NotContain("ORDER BY");
        (await query.CountAsync()).Should().Be(4);
    }

    // ── Includes ──
    [Fact]
    public async Task Apply_AppliesIncludes()
    {
        var query = SpecificationEvaluator.Apply<SpecTestEntity, int>(Source, new IncludingSpecification());

        var rows = await query.ToListAsync();

        rows.Should().ContainSingle();
        rows[0].Children.Select(c => c.Label).Should().BeEquivalentTo("one", "two");
    }

    [Fact]
    public void ApplyIncludes_WithACollectionNavigation_SwitchesToSplitQuery()
    {
        var withCollection = SpecificationEvaluator.ApplyIncludes(Source, ["Children"]).Expression.ToString();
        var withNothing = SpecificationEvaluator.ApplyIncludes(Source, []).Expression.ToString();

        withCollection.Should().Contain(
            "AsSplitQuery",
            "a collection include must auto-switch to split query so sibling collections do not multiply rows");
        withNothing.Should().NotContain("AsSplitQuery", "there is nothing to split without a collection include");
    }

    [Fact]
    public void ApplyIncludes_IgnoresBlankPaths()
    {
        var act = () => SpecificationEvaluator.ApplyIncludes(Source, [string.Empty, "   "]).ToQueryString();

        act.Should().NotThrow();
    }

    [Fact]
    public void ApplyIncludes_WithNullIncludes_Throws()
    {
        var act = () => SpecificationEvaluator.ApplyIncludes(Source, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Paging ──
    [Fact]
    public async Task Apply_AppliesSkipAndTake()
    {
        var query = SpecificationEvaluator.Apply<SpecTestEntity, int>(Source, new PagedSpecification());

        var ids = await query.Select(e => e.Id).ToListAsync();

        // Ordered by Id ascending, skip 1, take 2.
        ids.Should().Equal(2, 3);
    }

    [Fact]
    public async Task Apply_WithApplyShapeFalse_IgnoresOrderingAndPaging()
    {
        var query = SpecificationEvaluator.Apply<SpecTestEntity, int>(Source, new PagedSpecification(), applyShape: false);

        var sql = query.ToQueryString();

        sql.Should().NotContain("ORDER BY");
        sql.Should().NotContain("OFFSET");
        (await query.CountAsync()).Should().Be(4, "the count must see every matching row, not one page of them");
    }

    // ── Test specifications ──
    private sealed class BetaSpecification : Specification<SpecTestEntity, int>
    {
        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => e.Name == "beta";
    }

    private sealed class OrderedSpecification : QuerySpecification<SpecTestEntity, int>
    {
        public OrderedSpecification()
        {
            AddOrderBy(e => e.Name);
            AddOrderBy(e => e.Rank, descending: true);
        }

        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => true;
    }

    private sealed class RankDescendingSpecification : QuerySpecification<SpecTestEntity, int>
    {
        public RankDescendingSpecification() => AddOrderBy(e => e.Rank, descending: true);

        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => true;
    }

    private sealed class UnorderedQuerySpecification : QuerySpecification<SpecTestEntity, int>
    {
        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => true;
    }

    private sealed class IncludingSpecification : QuerySpecification<SpecTestEntity, int>
    {
        public IncludingSpecification() => AddInclude(nameof(SpecTestEntity.Children));

        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => e.Id == 1;
    }

    private sealed class PagedSpecification : QuerySpecification<SpecTestEntity, int>
    {
        public PagedSpecification()
        {
            AddOrderBy(e => e.Id);
            ApplyPaging(skip: 1, take: 2);
        }

        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => true;
    }
}
