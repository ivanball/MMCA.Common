using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class LongFilterStrategyTests
{
    private sealed class Item
    {
        public long Count { get; set; }

        public long? Score { get; set; }
    }

    private static IQueryable<Item> Items() =>
        new List<Item>
        {
            new() { Count = 5_000_000_000L, Score = 5_000_000_000L },
            new() { Count = 10_000_000_000L, Score = null },
            new() { Count = 15_000_000_000L, Score = 15_000_000_000L },
            new() { Count = 20_000_000_000L, Score = null },
        }.AsQueryable();

    private static readonly Dictionary<string, string> EmptyMap = [];

    private static IQueryable<Item> Filter(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Count"] = (op, value) },
            EmptyMap);

    private static IQueryable<Item> FilterScore(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Score"] = (op, value) },
            EmptyMap);

    // ── EQUALS ──
    [Fact]
    public void Equals_ReturnsExactMatch() =>
        Filter("EQUALS", "10000000000").Should().ContainSingle(i => i.Count == 10_000_000_000L);

    // ── NOT EQUALS ──
    [Fact]
    public void NotEquals_ExcludesMatch() =>
        Filter("NOT EQUALS", "10000000000").Should().HaveCount(3);

    // ── Comparisons ──
    [Fact]
    public void GreaterThan_ReturnsItemsAboveValue() =>
        Filter("GREATER THAN", "10000000000").Should().HaveCount(2);

    [Fact]
    public void LessThanOrEqual_IncludesBoundary() =>
        Filter("LESS THAN OR EQUAL", "10000000000").Should().HaveCount(2);

    // ── Invalid value ──
    [Fact]
    public void InvalidValue_ReturnsAll() =>
        Filter("EQUALS", "not-a-number").Should().HaveCount(4);

    // ── Unknown operator ──
    [Fact]
    public void UnknownOperator_ReturnsAll() =>
        Filter("CONTAINS", "10000000000").Should().HaveCount(4);

    // ── IN ──
    [Fact]
    public void In_ReturnsItemsMatchingAnyListedValue() =>
        Filter("IN", "5000000000,15000000000").Select(i => i.Count)
            .Should().BeEquivalentTo([5_000_000_000L, 15_000_000_000L]);

    [Fact]
    public void In_SkipsUnparseableValues() =>
        Filter("IN", "5000000000,not-a-number,20000000000").Should().HaveCount(2);

    // ── BETWEEN ──
    [Fact]
    public void Between_ReturnsInclusiveRange() =>
        Filter("BETWEEN", "10000000000,15000000000").Select(i => i.Count)
            .Should().BeEquivalentTo([10_000_000_000L, 15_000_000_000L]);

    [Fact]
    public void Between_WithSingleValue_ReturnsAll() =>
        Filter("BETWEEN", "10000000000").Should().HaveCount(4);

    // ── IS EMPTY / IS NOT EMPTY (nullable) ──
    [Fact]
    public void IsEmpty_ReturnsNullScores() =>
        FilterScore("IS EMPTY", string.Empty).Should().HaveCount(2);

    [Fact]
    public void IsNotEmpty_ReturnsNonNullScores() =>
        FilterScore("IS NOT EMPTY", string.Empty).Should().HaveCount(2);
}
