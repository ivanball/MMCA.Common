using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Infrastructure.Tests.Persistence.Specifications;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// Generated-SQL learning tests for <c>KeysetQueryBuilder</c>. Keyset paging is correct only because
/// of two properties nothing else in the suite looks at: the order is TOTAL (the identifier
/// tie-break) and the seek predicate is a composite comparison against the boundary row. Both live
/// in an expression tree, so a refactor can drop either and every behavioral test still passes
/// against a small, tie-free data set. These tests read the statement EF actually emits, so the
/// regression fails here instead of in production, at page seven, as a duplicated or vanished row.
/// </summary>
/// <remarks>
/// The provider is SQLite rather than SQL Server so the run needs no database (see the sibling
/// specification tests). <c>ToQueryString</c> renders the statement without executing it, which is
/// what makes the SHAPE assertable; identifier quoting is provider specific, so the assertions match
/// on the column names and the operators rather than on a full literal statement.
/// </remarks>
public sealed class KeysetQueryBuilderSqlTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SpecificationTestDbContext _context;

    public KeysetQueryBuilderSqlTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SpecificationTestDbContext(
            new DbContextOptionsBuilder<SpecificationTestDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private IQueryable<SpecTestEntity> Source => _context.Entities.AsNoTracking();

    private static PropertyInfo RankProperty =>
        typeof(SpecTestEntity).GetProperty(nameof(SpecTestEntity.Rank))!;

    private static PropertyInfo NameProperty =>
        typeof(SpecTestEntity).GetProperty(nameof(SpecTestEntity.Name))!;

    /// <summary>
    /// Strips the parameter-declaration preamble <c>ToQueryString</c> prepends (SQLite emits
    /// <c>.param set @p '3'</c> so the statement can be replayed in a shell). The declarations
    /// necessarily carry the raw values; every assertion here is about the STATEMENT.
    /// </summary>
    /// <param name="sql">The rendered query string.</param>
    /// <returns>The statement without the preamble.</returns>
    private static string Statement(string sql) =>
        string.Join(
            '\n',
            sql.Split('\n').Where(l => !l.TrimStart().StartsWith(".param", StringComparison.Ordinal)))
        .Trim();

    private static string OrderByClause(string sql)
    {
        var index = sql.IndexOf("ORDER BY", StringComparison.Ordinal);
        return index < 0 ? string.Empty : sql[index..];
    }

    // ── ORDER BY: the tie-break is the whole point ──
    [Fact]
    public void ApplyOrdering_WithASortKey_OrdersByTheKeyThenTheIdentifier()
    {
        var sql = Statement(
            KeysetQueryBuilder.ApplyOrdering<SpecTestEntity, int>(Source, RankProperty, descending: false)
                .ToQueryString());

        var orderBy = OrderByClause(sql);

        orderBy.Should().NotBeEmpty();
        orderBy.Should().Contain("\"Rank\"");
        orderBy.Should().Contain(
            "\"Id\"",
            "without the identifier tie-break two rows sharing a sort value can swap places between pages, "
            + "so one is returned twice and another never");
        orderBy.IndexOf("\"Rank\"", StringComparison.Ordinal).Should().BeLessThan(
            orderBy.IndexOf("\"Id\"", StringComparison.Ordinal),
            "the identifier breaks ties within the sort key, so it must come second");
    }

    [Fact]
    public void ApplyOrdering_Descending_ReversesTheSortKeyOnly()
    {
        var sql = Statement(
            KeysetQueryBuilder.ApplyOrdering<SpecTestEntity, int>(Source, RankProperty, descending: true)
                .ToQueryString());

        var orderBy = OrderByClause(sql);

        orderBy.Should().Contain("\"Rank\" DESC");
        orderBy.Should().NotContain(
            "\"Id\" DESC",
            "the tie-break always ascends: the seek predicate compares the identifier with > in both directions");
    }

    [Fact]
    public void ApplyOrdering_WithNoSortKey_OrdersByTheIdentifierAlone()
    {
        var sql = Statement(
            KeysetQueryBuilder.ApplyOrdering<SpecTestEntity, int>(Source, sortProperty: null, descending: false)
                .ToQueryString());

        var orderBy = OrderByClause(sql);

        orderBy.Should().Contain("\"Id\"");
        orderBy.Should().NotContain("\"Rank\"", "with no sort key the identifier IS the sort key");
    }

    [Fact]
    public void ApplyOrdering_WithNoSortKeyDescending_ReversesTheIdentifier()
    {
        var sql = Statement(
            KeysetQueryBuilder.ApplyOrdering<SpecTestEntity, int>(Source, sortProperty: null, descending: true)
                .ToQueryString());

        OrderByClause(sql).Should().Contain("\"Id\" DESC");
    }

    // ── WHERE: the composite seek predicate ──
    [Fact]
    public void BuildSeekPredicate_Forward_EmitsTheCompositeComparison()
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<SpecTestEntity, int>(
            RankProperty, sortValue: 3, lastId: 7, descending: false);

        var sql = Statement(Source.Where(seek).ToQueryString());

        sql.Should().MatchRegex(
            @"""Rank"" > .+ OR .*""Rank"" = .+ AND .*""Id"" > ",
            "a forward page is everything past the boundary row: a later sort value, or the same sort "
            + "value with a later identifier");
    }

    [Fact]
    public void BuildSeekPredicate_Backward_ReversesOnlyTheSortHalf()
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<SpecTestEntity, int>(
            RankProperty, sortValue: 3, lastId: 7, descending: true);

        var sql = Statement(Source.Where(seek).ToQueryString());

        sql.Should().MatchRegex(
            @"""Rank"" < .+ OR .*""Rank"" = .+ AND .*""Id"" > ",
            "a descending page walks the sort key backwards while the identifier tie-break still "
            + "advances, which is what the ascending tie-break in ApplyOrdering assumes");
        sql.Should().NotContain("\"Id\" < ", "reversing the tie-break would re-read the boundary row's ties");
    }

    [Fact]
    public void BuildSeekPredicate_WithNoSortKey_SeeksOnTheIdentifierAlone()
    {
        var forward = Statement(Source.Where(
            KeysetQueryBuilder.BuildSeekPredicate<SpecTestEntity, int>(null, null, lastId: 7, descending: false))
            .ToQueryString());
        var backward = Statement(Source.Where(
            KeysetQueryBuilder.BuildSeekPredicate<SpecTestEntity, int>(null, null, lastId: 7, descending: true))
            .ToQueryString());

        forward.Should().Contain("\"Id\" > ");
        backward.Should().Contain(
            "\"Id\" < ",
            "with no sort key the identifier IS the sort key, so it follows the requested direction");
    }

    [Fact]
    public void BuildSeekPredicate_WithANullBoundaryOnANullableKey_SaysSoExplicitly()
    {
        var categoryProperty = typeof(SpecTestEntity).GetProperty(nameof(SpecTestEntity.Category))!;

        var seek = KeysetQueryBuilder.BuildSeekPredicate<SpecTestEntity, int>(
            categoryProperty, sortValue: null, lastId: 7, descending: false);

        var sql = Statement(Source.Where(seek).ToQueryString());

        sql.Should().Contain(
            "IS NOT NULL",
            "a SQL comparison against NULL is unknown, so a null boundary has to be spelled out or the "
            + "page silently drops every remaining row");
    }

    // ── Parameterization ──
    [Fact]
    public void BuildSeekPredicate_KeepsTheBoundaryValuesOutOfTheStatement()
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<SpecTestEntity, int>(
            RankProperty, sortValue: 4242, lastId: 8484, descending: false);

        var sql = Statement(Source.Where(seek).ToQueryString());

        sql.Should().NotContain(
            "4242",
            "an inlined boundary value gives every page of every cursor its own statement text, which "
            + "defeats plan reuse and misses EF's compiled-query cache");
        sql.Should().NotContain("8484");
        sql.Should().Contain("@", "the boundary must reach the database as a parameter");
    }

    [Fact]
    public void TwoCursorsOfTheSamePage_ProduceIdenticalSql()
    {
        // The plan-reuse invariant stated directly: only the parameter VALUES may differ between two
        // requests that page the same way.
        var first = Statement(Source.Where(
            KeysetQueryBuilder.BuildSeekPredicate<SpecTestEntity, int>(RankProperty, 1, 1, descending: false))
            .ToQueryString());
        var second = Statement(Source.Where(
            KeysetQueryBuilder.BuildSeekPredicate<SpecTestEntity, int>(RankProperty, 99, 99, descending: false))
            .ToQueryString());

        second.Should().Be(first);
    }

    [Fact]
    public void BuildSeekPredicate_OnAStringKey_KeepsTheBoundaryOutOfTheStatement()
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<SpecTestEntity, int>(
            NameProperty, sortValue: "zeta", lastId: 7, descending: false);

        var sql = Statement(Source.Where(seek).ToQueryString());

        sql.Should().NotContain("'zeta'", "a string boundary is a parameter like any other");
    }
}
