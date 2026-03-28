using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class IntFilterStrategyTests
{
    private sealed class Item
    {
        public int Count { get; set; }
    }

    private static IQueryable<Item> Items() =>
        new List<Item>
        {
            new() { Count = 5 },
            new() { Count = 10 },
            new() { Count = 15 },
            new() { Count = 20 },
        }.AsQueryable();

    private static readonly Dictionary<string, string> EmptyMap = [];

    private static IQueryable<Item> Filter(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Count"] = (op, value) },
            EmptyMap);

    // ── EQUALS ──
    [Fact]
    public void Equals_ReturnsExactMatch() =>
        Filter("EQUALS", "10").Should().ContainSingle(i => i.Count == 10);

    // ── NOT EQUALS ──
    [Fact]
    public void NotEquals_ExcludesMatch() =>
        Filter("NOT EQUALS", "10").Should().HaveCount(3);

    // ── GREATER THAN ──
    [Fact]
    public void GreaterThan_ReturnsItemsAboveValue() =>
        Filter("GREATER THAN", "10").Should().HaveCount(2);

    // ── LESS THAN ──
    [Fact]
    public void LessThan_ReturnsItemsBelowValue() =>
        Filter("LESS THAN", "10").Should().ContainSingle(i => i.Count == 5);

    // ── GREATER THAN OR EQUAL ──
    [Fact]
    public void GreaterThanOrEqual_IncludesBoundary() =>
        Filter("GREATER THAN OR EQUAL", "10").Should().HaveCount(3);

    // ── LESS THAN OR EQUAL ──
    [Fact]
    public void LessThanOrEqual_IncludesBoundary() =>
        Filter("LESS THAN OR EQUAL", "10").Should().HaveCount(2);

    // ── Invalid value ──
    [Fact]
    public void InvalidValue_ReturnsAll() =>
        Filter("EQUALS", "not-a-number").Should().HaveCount(4);

    // ── Unknown operator ──
    [Fact]
    public void UnknownOperator_ReturnsAll() =>
        Filter("CONTAINS", "10").Should().HaveCount(4);
}
