using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class DateTimeFilterStrategyTests
{
    private sealed class Item
    {
        public DateTime? Created { get; set; }

        public DateTime Occurred { get; set; }
    }

    private static IQueryable<Item> Items() =>
        new List<Item>
        {
            new() { Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Occurred = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { Created = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), Occurred = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { Created = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), Occurred = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { Created = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), Occurred = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { Created = null, Occurred = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) },
        }.AsQueryable();

    private static readonly Dictionary<string, string> EmptyMap = [];

    private static IQueryable<Item> Filter(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Created"] = (op, value) },
            EmptyMap);

    private static IQueryable<Item> FilterOccurred(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Occurred"] = (op, value) },
            EmptyMap);

    // ── IS ──
    [Fact]
    public void Is_ReturnsExactMatch() =>
        Filter("IS", "2026-02-01").Should().ContainSingle();

    // ── IS NOT ──
    [Fact]
    public void IsNot_ExcludesMatch() =>
        Filter("IS NOT", "2026-02-01").Should().HaveCount(4);

    // ── IS AFTER ──
    [Fact]
    public void IsAfter_ReturnsDatesAfter() =>
        Filter("IS AFTER", "2026-02-01").Should().HaveCount(2);

    // ── IS ON OR AFTER ──
    [Fact]
    public void IsOnOrAfter_IncludesExactDate() =>
        Filter("IS ON OR AFTER", "2026-02-01").Should().HaveCount(3);

    // ── IS BEFORE ──
    [Fact]
    public void IsBefore_ReturnsDatesBeforeValue() =>
        Filter("IS BEFORE", "2026-02-01").Should().ContainSingle();

    // ── IS ON OR BEFORE ──
    [Fact]
    public void IsOnOrBefore_IncludesExactDate() =>
        Filter("IS ON OR BEFORE", "2026-02-01").Should().HaveCount(2);

    // ── IS EMPTY ──
    [Fact]
    public void IsEmpty_ReturnsNullDates() =>
        Filter("IS EMPTY", string.Empty).Should().ContainSingle();

    // ── IS NOT EMPTY ──
    [Fact]
    public void IsNotEmpty_ReturnsNonNullDates() =>
        Filter("IS NOT EMPTY", string.Empty).Should().HaveCount(4);

    // ── Invalid date ──
    [Fact]
    public void InvalidDate_ReturnsAll() =>
        Filter("IS", "not-a-date").Should().HaveCount(5);

    // ── IN (non-nullable column; matches EF IN-translation, LINQ Dynamic binds the set to a non-null type) ──
    [Fact]
    public void In_ReturnsItemsMatchingAnyListedValue() =>
        FilterOccurred("IN", "2026-01-01,2026-03-01").Should().HaveCount(2);

    [Fact]
    public void In_SkipsUnparseableValues() =>
        FilterOccurred("IN", "2026-01-01,not-a-date").Should().ContainSingle();

    // ── BETWEEN ──
    [Fact]
    public void Between_ReturnsInclusiveRange() =>
        Filter("BETWEEN", "2026-02-01,2026-03-01").Should().HaveCount(2);

    [Fact]
    public void Between_WithSingleValue_ReturnsAll() =>
        Filter("BETWEEN", "2026-02-01").Should().HaveCount(5);

    // ── Unknown operator ──
    [Fact]
    public void UnknownOperator_ReturnsAll() =>
        Filter("IS WITHIN", "2026-02-01").Should().HaveCount(5);
}
