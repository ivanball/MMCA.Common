using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class DecimalFilterStrategyTests
{
    private sealed class Item
    {
        public decimal Price { get; set; }

        public decimal? Discount { get; set; }
    }

    private static IQueryable<Item> Items() =>
        new List<Item>
        {
            new() { Price = 9.99m, Discount = 1.00m },
            new() { Price = 19.99m, Discount = null },
            new() { Price = 29.99m, Discount = 5.00m },
            new() { Price = 49.99m, Discount = null },
        }.AsQueryable();

    private static readonly Dictionary<string, string> EmptyMap = [];

    private static IQueryable<Item> Filter(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Price"] = (op, value) },
            EmptyMap);

    private static IQueryable<Item> FilterDiscount(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Discount"] = (op, value) },
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

    // ── IN ──
    [Fact]
    public void In_ReturnsItemsMatchingAnyListedValue() =>
        Filter("IN", "9.99,29.99").Select(i => i.Price).Should().BeEquivalentTo([9.99m, 29.99m]);

    [Fact]
    public void In_SkipsUnparseableValues() =>
        Filter("IN", "9.99,not-a-number,49.99").Should().HaveCount(2);

    [Fact]
    public void In_WithNoParseableValues_ReturnsAll() =>
        Filter("IN", "a,b").Should().HaveCount(4);

    // ── BETWEEN ──
    [Fact]
    public void Between_ReturnsInclusiveRange() =>
        Filter("BETWEEN", "19.99,29.99").Select(i => i.Price).Should().BeEquivalentTo([19.99m, 29.99m]);

    [Fact]
    public void Between_WithSingleValue_ReturnsAll() =>
        Filter("BETWEEN", "19.99").Should().HaveCount(4);

    // ── IS EMPTY / IS NOT EMPTY (nullable) ──
    [Fact]
    public void IsEmpty_ReturnsNullDiscounts() =>
        FilterDiscount("IS EMPTY", string.Empty).Should().HaveCount(2);

    [Fact]
    public void IsNotEmpty_ReturnsNonNullDiscounts() =>
        FilterDiscount("IS NOT EMPTY", string.Empty).Should().HaveCount(2);

    // ── Unknown operator ──
    [Fact]
    public void UnknownOperator_ReturnsAll() =>
        Filter("CONTAINS", "19.99").Should().HaveCount(4);
}
