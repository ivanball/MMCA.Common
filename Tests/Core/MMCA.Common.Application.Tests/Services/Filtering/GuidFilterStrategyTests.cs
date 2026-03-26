using FluentAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class GuidFilterStrategyTests
{
    private sealed class Item
    {
        public Guid Tag { get; set; }
    }

    private const string Guid1String = "11111111-1111-1111-1111-111111111111";
    private const string Guid2String = "22222222-2222-2222-2222-222222222222";

    private static IQueryable<Item> Items() =>
        new List<Item>
        {
            new() { Tag = new Guid(0x11111111, 0x1111, 0x1111, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11) },
            new() { Tag = new Guid(0x22222222, 0x2222, 0x2222, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22) },
            new() { Tag = new Guid(0x33333333, 0x3333, 0x3333, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33) },
            new() { Tag = new Guid(0x44444444, 0x4444, 0x4444, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44) },
        }.AsQueryable();

    private static readonly Dictionary<string, string> EmptyMap = [];

    private static IQueryable<Item> Filter(string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)> { ["Tag"] = (op, value) },
            EmptyMap);

    // ── EQUALS ──
    [Fact]
    public void Equals_ReturnsExactMatch() =>
        Filter("EQUALS", Guid2String).Should().ContainSingle();

    // ── NOT EQUALS ──
    [Fact]
    public void NotEquals_ExcludesMatch() =>
        Filter("NOT EQUALS", Guid2String).Should().HaveCount(3);

    // ── Invalid value ──
    [Fact]
    public void InvalidValue_ReturnsAll() =>
        Filter("EQUALS", "not-a-guid").Should().HaveCount(4);

    // ── Unknown operator ──
    [Fact]
    public void UnknownOperator_ReturnsAll() =>
        Filter("CONTAINS", Guid1String).Should().HaveCount(4);
}
