using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class IntFilterStrategyTests
{
    private sealed class Item
    {
        public int Count { get; set; }

        public int? Score { get; set; }
    }

    private static IQueryable<Item> Items() =>
        new List<Item>
        {
            new() { Count = 5, Score = 5 },
            new() { Count = 10, Score = null },
            new() { Count = 15, Score = 15 },
            new() { Count = 20, Score = null },
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

    // ── IN ──
    [Fact]
    public void In_ReturnsItemsMatchingAnyListedValue() =>
        Filter("IN", "5,15").Select(i => i.Count).Should().BeEquivalentTo([5, 15]);

    [Fact]
    public void In_TrimsWhitespaceAroundValues() =>
        Filter("IN", " 10 , 20 ").Should().HaveCount(2);

    [Fact]
    public void In_SkipsUnparseableValues() =>
        Filter("IN", "5,not-a-number,20").Select(i => i.Count).Should().BeEquivalentTo([5, 20]);

    [Fact]
    public void In_WithNoParseableValues_ReturnsAll() =>
        Filter("IN", "a,b,c").Should().HaveCount(4);

    [Fact]
    public void In_WithEmptyValue_ReturnsAll() =>
        Filter("IN", string.Empty).Should().HaveCount(4);

    // ── BETWEEN ──
    [Fact]
    public void Between_ReturnsInclusiveRange() =>
        Filter("BETWEEN", "10,15").Select(i => i.Count).Should().BeEquivalentTo([10, 15]);

    [Fact]
    public void Between_IncludesBothBounds() =>
        Filter("BETWEEN", "5,20").Should().HaveCount(4);

    [Fact]
    public void Between_WithSingleValue_ReturnsAll() =>
        Filter("BETWEEN", "10").Should().HaveCount(4);

    [Fact]
    public void Between_WithUnparseableBound_ReturnsAll() =>
        Filter("BETWEEN", "10,not-a-number").Should().HaveCount(4);

    // ── IS EMPTY / IS NOT EMPTY (nullable) ──
    [Fact]
    public void IsEmpty_ReturnsNullScores() =>
        FilterScore("IS EMPTY", string.Empty).Should().HaveCount(2);

    [Fact]
    public void IsNotEmpty_ReturnsNonNullScores() =>
        FilterScore("IS NOT EMPTY", string.Empty).Should().HaveCount(2);

    // ── Culture independence: the filter DSL is a wire format, not user input (L10) ──
    private static IQueryable<Item> SignedItems() =>
        new List<Item>
        {
            new() { Count = -5, Score = -5 },
            new() { Count = 5, Score = 5 },
        }.AsQueryable();

    private static IQueryable<Item> FilterSigned(string op, string value) =>
        QueryFilterService.ApplyFilters(
            SignedItems(),
            new Dictionary<string, (string, string)> { ["Count"] = (op, value) },
            EmptyMap);

    /// <summary>
    /// Runs <paramref name="assert"/> under a culture whose negative sign is not "-". API hosts call
    /// UseRequestLocalization, so a culture-sensitive parse made the int strategy track the request
    /// culture while the decimal, long and date strategies stayed invariant. A failed parse falls
    /// through to the UNFILTERED query, which silently widens the result set.
    /// </summary>
    private static void UnderACultureWithACustomNegativeSign(Action assert)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
            culture.NumberFormat.NegativeSign = "~";
            CultureInfo.CurrentCulture = culture;
            assert();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // NOTE: these assert the exact row COUNT, not just that a matching row is present. A failed
    // parse falls through to the UNFILTERED query, which still contains the expected row, so
    // ContainSingle(predicate) would pass against the very bug these tests exist to catch.
    [Fact]
    public void Equals_UnderAForeignNegativeSign_StillParsesTheInvariantValue() =>
        UnderACultureWithACustomNegativeSign(() =>
            FilterSigned("EQUALS", "-5").Should().ContainSingle().Which.Count.Should().Be(-5));

    [Fact]
    public void In_UnderAForeignNegativeSign_StillParsesEveryInvariantValue() =>
        UnderACultureWithACustomNegativeSign(() =>
            FilterSigned("IN", "-5,5").Should().HaveCount(2));

    [Fact]
    public void Between_UnderAForeignNegativeSign_StillParsesBothBounds() =>
        UnderACultureWithACustomNegativeSign(() =>
            FilterSigned("BETWEEN", "-5,0").Should().ContainSingle().Which.Count.Should().Be(-5));
}
