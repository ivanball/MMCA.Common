using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class StringFilterStrategyTests
{
    private sealed class Item
    {
        public string Name { get; set; } = string.Empty;
    }

    private static IQueryable<Item> Items() =>
        new List<Item>
        {
            new() { Name = "Alice" },
            new() { Name = "Bob" },
            new() { Name = "Charlie" },
            new() { Name = "David" },
            new() { Name = string.Empty },
        }.AsQueryable();

    private static readonly Dictionary<string, string> EmptyMap = [];

    private static IQueryable<Item> Filter(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Name"] = (op, value) },
            EmptyMap);

    // ── CONTAINS ──
    [Fact]
    public void Contains_ReturnsMatchingItems() =>
        Filter("CONTAINS", "li").Should().HaveCount(2); // Alice, Charlie

    // ── NOT CONTAINS ──
    [Fact]
    public void NotContains_ExcludesMatchingItems() =>
        Filter("NOT CONTAINS", "li").Should().HaveCount(3); // Bob, David, empty

    // ── EQUALS ──
    [Fact]
    public void Equals_ReturnsExactMatch() =>
        Filter("EQUALS", "Bob").Should().ContainSingle(i => i.Name == "Bob");

    // ── NOT EQUALS ──
    [Fact]
    public void NotEquals_ExcludesExactMatch() =>
        Filter("NOT EQUALS", "Bob").Should().HaveCount(4);

    // ── STARTS WITH ──
    [Fact]
    public void StartsWith_FiltersByPrefix() =>
        Filter("STARTS WITH", "Ch").Should().ContainSingle(i => i.Name == "Charlie");

    // ── ENDS WITH ──
    [Fact]
    public void EndsWith_FiltersBySuffix() =>
        Filter("ENDS WITH", "id").Should().ContainSingle(i => i.Name == "David");

    // ── IS EMPTY ──
    [Fact]
    public void IsEmpty_ReturnsEmptyStrings() =>
        Filter("IS EMPTY", string.Empty).Should().ContainSingle();

    // ── IS NOT EMPTY ──
    [Fact]
    public void IsNotEmpty_ExcludesEmptyStrings() =>
        Filter("IS NOT EMPTY", string.Empty).Should().HaveCount(4);

    // ── Unknown operator ──
    [Fact]
    public void UnknownOperator_ReturnsAll() =>
        Filter("BETWEEN", "Alice").Should().HaveCount(5);
}
