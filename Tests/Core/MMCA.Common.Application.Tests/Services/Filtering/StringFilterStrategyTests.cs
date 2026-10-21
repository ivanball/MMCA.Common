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

    // -- CanParseValue (L44) --
    [Theory]
    [InlineData("IN", " , ", false)]
    [InlineData("IN", "a, ,b", true)]
    [InlineData("EQUALS", "", true)]
    public void CanParseValue_RejectsOnlyAnInListWithNoUsableItem(string op, string value, bool expected) =>
        new StringFilterStrategy().CanParseValue(op, value).Should().Be(expected);

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

    // -- IS EMPTY / IS NOT EMPTY over nullable values and supplementary characters --
    private sealed class LabelledItem
    {
        public string? Label { get; set; }
    }

    private static readonly string?[] PresenceValues = [null, string.Empty, "a", "\U0001F600", "x\U0001F600"];

    private static readonly string?[] EmptyValues = [null, string.Empty];

    private static readonly string?[] NonEmptyValues = ["a", "\U0001F600", "x\U0001F600"];

    private static List<string?> FilterLabels(string op) =>
        [.. QueryFilterService.ApplyFilters(
                PresenceValues.Select(v => new LabelledItem { Label = v }).AsQueryable(),
                new Dictionary<string, (string, string)> { ["Label"] = (op, string.Empty) },
                EmptyMap)
            .Select(i => i.Label)];

    [Fact]
    public void IsEmpty_MatchesNullAndEmptyOnly() =>
        FilterLabels("IS EMPTY").Should().BeEquivalentTo(EmptyValues);

    [Theory]
    [InlineData("a")]
    [InlineData("\U0001F600")]
    [InlineData("x\U0001F600")]
    public void IsEmpty_DoesNotMatchNonEmptyValues_IncludingEmojiOnly(string value) =>
        FilterLabels("IS EMPTY").Should().NotContain(value);

    [Fact]
    public void IsNotEmpty_IsTheExactComplementOfIsEmpty()
    {
        var empty = FilterLabels("IS EMPTY");
        var notEmpty = FilterLabels("IS NOT EMPTY");

        notEmpty.Should().BeEquivalentTo(NonEmptyValues);
        notEmpty.Should().NotIntersectWith(empty);
        empty.Concat(notEmpty).Should().BeEquivalentTo(PresenceValues);
    }

    // ── Unknown operator ──
    [Fact]
    public void UnknownOperator_ReturnsAll() =>
        Filter("BETWEEN", "Alice").Should().HaveCount(5);

    // ── IN ──
    [Fact]
    public void In_ReturnsItemsMatchingAnyListedValue() =>
        Filter("IN", "Alice,David").Select(i => i.Name).Should()
            .HaveCount(2).And.Contain("Alice").And.Contain("David");

    [Fact]
    public void In_TrimsWhitespaceAroundValues() =>
        Filter("IN", " Bob , Charlie ").Should().HaveCount(2);

    [Fact]
    public void In_WithEmptyValue_ReturnsAll() =>
        Filter("IN", string.Empty).Should().HaveCount(5);
}
