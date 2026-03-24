using FluentAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class DecimalFilterStrategyTests
{
    private sealed class Item
    {
        public decimal Price { get; set; }
    }

    private static IQueryable<Item> Items() =>
        new List<Item>
        {
            new() { Price = 9.99m },
            new() { Price = 19.99m },
            new() { Price = 29.99m },
            new() { Price = 49.99m },
        }.AsQueryable();

    private static readonly Dictionary<string, string> EmptyMap = [];

    private static IQueryable<Item> Filter(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Price"] = (op, value) },
            EmptyMap);

    // ── EQUALS ──
    [Fact]
    public void Equals_ReturnsExactMatch() =>
        Filter("EQUALS", "19.99").Should().ContainSingle(i => i.Price == 19.99m);

    // ── NOT EQUALS ──
    [Fact]
    public void NotEquals_ExcludesMatch() =>
        Filter("NOT EQUALS", "19.99").Should().HaveCount(3);

    // ── GREATER THAN ──
    [Fact]
    public void GreaterThan_ReturnsItemsAboveValue() =>
        Filter("GREATER THAN", "19.99").Should().HaveCount(2);

    // ── GREATER THAN OR EQUAL ──
    [Fact]
    public void GreaterThanOrEqual_IncludesBoundary() =>
        Filter("GREATER THAN OR EQUAL", "19.99").Should().HaveCount(3);

    // ── LESS THAN ──
    [Fact]
    public void LessThan_ReturnsItemsBelowValue() =>
        Filter("LESS THAN", "19.99").Should().ContainSingle(i => i.Price == 9.99m);

    // ── LESS THAN OR EQUAL ──
    [Fact]
    public void LessThanOrEqual_IncludesBoundary() =>
        Filter("LESS THAN OR EQUAL", "19.99").Should().HaveCount(2);

    // ── Invalid value ──
    [Fact]
    public void InvalidDecimalValue_ReturnsAll() =>
        Filter("EQUALS", "not-a-number").Should().HaveCount(4);

    // ── Unknown operator ──
    [Fact]
    public void UnknownOperator_ReturnsAll() =>
        Filter("CONTAINS", "19.99").Should().HaveCount(4);
}
