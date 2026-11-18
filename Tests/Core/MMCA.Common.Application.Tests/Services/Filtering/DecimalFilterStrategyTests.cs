using AwesomeAssertions;
using MMCA.Common.Application.Tests.Services.Filtering.Numeric;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class DecimalFilterStrategyTests : NumericFilterStrategyTestsBase<decimal>
{
    protected override IReadOnlyList<decimal> Values { get; } = [9.99m, 19.99m, 29.99m, 49.99m];

    // ── GREATER THAN OR EQUAL ──
    [Fact]
    public void GreaterThanOrEqual_IncludesBoundary() =>
        Filter("GREATER THAN OR EQUAL", "19.99").Should().HaveCount(3);

    // ── LESS THAN ──
    [Fact]
    public void LessThan_ReturnsItemsBelowValue() =>
        Filter("LESS THAN", "19.99").Should().ContainSingle(i => i.Count == 9.99m);

    // ── IN ──
    [Fact]
    public void In_WithNoParseableValues_ReturnsAll() =>
        Filter("IN", "a,b").Should().HaveCount(4);
}
