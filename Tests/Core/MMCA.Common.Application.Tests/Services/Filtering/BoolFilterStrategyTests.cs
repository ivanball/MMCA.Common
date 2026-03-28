using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class BoolFilterStrategyTests
{
    private sealed class Item
    {
        public bool IsActive { get; set; }
    }

    private static IQueryable<Item> Items() =>
        new List<Item>
        {
            new() { IsActive = true },
            new() { IsActive = false },
            new() { IsActive = true },
            new() { IsActive = false },
        }.AsQueryable();

    private static readonly Dictionary<string, string> EmptyMap = [];

    private static IQueryable<Item> Filter(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["IsActive"] = (op, value) },
            EmptyMap);

    // ── IS true ──
    [Fact]
    public void Is_True_ReturnsActiveItems() =>
        Filter("IS", "true").Should().HaveCount(2);

    // ── IS false ──
    [Fact]
    public void Is_False_ReturnsInactiveItems() =>
        Filter("IS", "false").Should().HaveCount(2);

    // ── Invalid value ──
    [Fact]
    public void InvalidValue_ReturnsAll() =>
        Filter("IS", "maybe").Should().HaveCount(4);

    // ── Unknown operator ──
    [Fact]
    public void UnknownOperator_ReturnsAll() =>
        Filter("EQUALS", "true").Should().HaveCount(4);
}
