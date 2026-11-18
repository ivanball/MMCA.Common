using System.Globalization;
using System.Numerics;
using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering.Numeric;

/// <summary>
/// The numeric filter strategy contract shared by the int, long and decimal strategies: the six
/// comparisons, IN, BETWEEN, the nullable IS EMPTY pair, and the fall-through to the unfiltered query
/// on an unparseable value or an unknown operator. Each subclass supplies four ascending values of
/// its type; the base filters a four-row set built from them.
/// </summary>
/// <typeparam name="T">The numeric property type under test.</typeparam>
public abstract class NumericFilterStrategyTestsBase<T>
    where T : struct, INumber<T>
{
    /// <summary>
    /// Four strictly ascending values. Rows carry them in order on <see cref="Item.Count"/>; the
    /// nullable <see cref="Item.Score"/> is set on the first and third row and null on the others.
    /// </summary>
    protected abstract IReadOnlyList<T> Values { get; }

    // ── EQUALS ──
    [Fact]
    public void Equals_ReturnsExactMatch()
    {
        var expected = Values[1];
        Filter("EQUALS", Text(1)).Should().ContainSingle(i => i.Count.Equals(expected));
    }

    // ── NOT EQUALS ──
    [Fact]
    public void NotEquals_ExcludesMatch() =>
        Filter("NOT EQUALS", Text(1)).Should().HaveCount(3);

    // ── GREATER THAN ──
    [Fact]
    public void GreaterThan_ReturnsItemsAboveValue() =>
        Filter("GREATER THAN", Text(1)).Should().HaveCount(2);

    // ── LESS THAN OR EQUAL ──
    [Fact]
    public void LessThanOrEqual_IncludesBoundary() =>
        Filter("LESS THAN OR EQUAL", Text(1)).Should().HaveCount(2);

    // ── Invalid value ──
    [Fact]
    public void InvalidValue_ReturnsAll() =>
        Filter("EQUALS", "not-a-number").Should().HaveCount(4);

    // ── Unknown operator ──
    [Fact]
    public void UnknownOperator_ReturnsAll() =>
        Filter("CONTAINS", Text(1)).Should().HaveCount(4);

    // ── IN ──
    [Fact]
    public void In_ReturnsItemsMatchingAnyListedValue() =>
        Filter("IN", $"{Text(0)},{Text(2)}").Select(i => i.Count).Should().BeEquivalentTo([Values[0], Values[2]]);

    [Fact]
    public void In_SkipsUnparseableValues() =>
        Filter("IN", $"{Text(0)},not-a-number,{Text(3)}").Select(i => i.Count)
            .Should().BeEquivalentTo([Values[0], Values[3]]);

    // ── BETWEEN ──
    [Fact]
    public void Between_ReturnsInclusiveRange() =>
        Filter("BETWEEN", $"{Text(1)},{Text(2)}").Select(i => i.Count).Should().BeEquivalentTo([Values[1], Values[2]]);

    [Fact]
    public void Between_WithSingleValue_ReturnsAll() =>
        Filter("BETWEEN", Text(1)).Should().HaveCount(4);

    // ── IS EMPTY / IS NOT EMPTY (nullable) ──
    [Fact]
    public void IsEmpty_ReturnsNullScores() =>
        FilterScore("IS EMPTY", string.Empty).Should().HaveCount(2);

    [Fact]
    public void IsNotEmpty_ReturnsNonNullScores() =>
        FilterScore("IS NOT EMPTY", string.Empty).Should().HaveCount(2);

    /// <summary>Filters the four-row set on <see cref="Item.Count"/>.</summary>
    protected IQueryable<Item> Filter(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Count"] = (op, value) },
            new Dictionary<string, string>());

    /// <summary>Filters the four-row set on the nullable <see cref="Item.Score"/>.</summary>
    protected IQueryable<Item> FilterScore(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Score"] = (op, value) },
            new Dictionary<string, string>());

    /// <summary>The invariant wire form of <see cref="Values"/>[<paramref name="index"/>].</summary>
    protected string Text(int index) => Values[index].ToString(null, CultureInfo.InvariantCulture);

    private IQueryable<Item> Items() =>
        new List<Item>
        {
            new() { Count = Values[0], Score = Values[0] },
            new() { Count = Values[1], Score = null },
            new() { Count = Values[2], Score = Values[2] },
            new() { Count = Values[3], Score = null },
        }.AsQueryable();

    /// <summary>One filtered row: a required and a nullable property of the type under test.</summary>
    protected sealed class Item
    {
        public T Count { get; set; }

        public T? Score { get; set; }
    }
}
