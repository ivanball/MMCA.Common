using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public class QueryFilterServiceTests
{
    private sealed class Product
    {
        public string Name { get; set; } = string.Empty;
        public int Price { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedOn { get; set; }
        public DateTime? DeletedOn { get; set; }
        public int? NullablePrice { get; set; }
    }

    private static IQueryable<Product> Products() =>
        new List<Product>
        {
            new()
            {
                Name = "Widget", Price = 10, IsActive = true,
                CreatedOn = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                DeletedOn = null, NullablePrice = 10,
            },
            new()
            {
                Name = "Gadget", Price = 25, IsActive = false,
                CreatedOn = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                DeletedOn = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                NullablePrice = null,
            },
            new()
            {
                Name = "Widget Pro", Price = 50, IsActive = true,
                CreatedOn = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                DeletedOn = null, NullablePrice = 50,
            },
            new()
            {
                Name = string.Empty, Price = 0, IsActive = false,
                CreatedOn = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                DeletedOn = null, NullablePrice = null,
            },
        }.AsQueryable();

    private static readonly Dictionary<string, string> EmptyMap = [];

    private static IQueryable<Product> Filter(string property, string op, string value) =>
        QueryFilterService.ApplyFilters(Products(), new Dictionary<string, (string, string)> { [property] = (op, value) }, EmptyMap);

    // ── String strategy via ApplyFilters ──
    [Fact]
    public void String_Contains_FiltersCorrectly() =>
        Filter("Name", "CONTAINS", "Widget").Should().HaveCount(2);

    [Fact]
    public void String_NotContains_ExcludesMatches() =>
        Filter("Name", "NOT CONTAINS", "Widget").Should().HaveCount(2);

    [Fact]
    public void String_Equals_ReturnsExactMatch() =>
        Filter("Name", "EQUALS", "Gadget").Should().ContainSingle();

    [Fact]
    public void String_NotEquals_ExcludesExactMatch() =>
        Filter("Name", "NOT EQUALS", "Gadget").Should().HaveCount(3);

    [Fact]
    public void String_StartsWith_FiltersByPrefix() =>
        Filter("Name", "STARTS WITH", "Wid").Should().HaveCount(2);

    [Fact]
    public void String_EndsWith_FiltersBySuffix() =>
        Filter("Name", "ENDS WITH", "Pro").Should().ContainSingle();

    [Fact]
    public void String_IsEmpty_ReturnsEmptyStrings() =>
        Filter("Name", "IS EMPTY", string.Empty).Should().ContainSingle();

    [Fact]
    public void String_IsNotEmpty_ExcludesEmptyStrings() =>
        Filter("Name", "IS NOT EMPTY", string.Empty).Should().HaveCount(3);

    [Fact]
    public void String_UnknownOp_ReturnsAll() =>
        Filter("Name", "UNKNOWN_OP", "x").Should().HaveCount(4);

    // ── Int strategy via ApplyFilters ──
    [Fact]
    public void Int_Equals_ReturnsMatch() =>
        Filter("Price", "EQUALS", "25").Should().ContainSingle(p => p.Name == "Gadget");

    [Fact]
    public void Int_NotEquals_ExcludesMatch() =>
        Filter("Price", "NOT EQUALS", "25").Should().HaveCount(3);

    [Fact]
    public void Int_InvalidValue_ReturnsAll() =>
        Filter("Price", "EQUALS", "abc").Should().HaveCount(4);

    [Fact]
    public void Int_UnknownOp_ReturnsAll() =>
        Filter("Price", "GREATER_THAN", "10").Should().HaveCount(4);

    // ── Bool strategy via ApplyFilters ──
    [Fact]
    public void Bool_Is_True_ReturnsActiveItems() =>
        Filter("IsActive", "IS", "true").Should().HaveCount(2);

    [Fact]
    public void Bool_Is_False_ReturnsInactiveItems() =>
        Filter("IsActive", "IS", "false").Should().HaveCount(2);

    [Fact]
    public void Bool_InvalidValue_ReturnsAll() =>
        Filter("IsActive", "IS", "maybe").Should().HaveCount(4);

    [Fact]
    public void Bool_UnknownOp_ReturnsAll() =>
        Filter("IsActive", "EQUALS", "true").Should().HaveCount(4);

    // ── DateTime strategy via ApplyFilters ──
    [Fact]
    public void DateTime_Is_ReturnsExactMatch() =>
        Filter("CreatedOn", "IS", "2024-06-15").Should().ContainSingle();

    [Fact]
    public void DateTime_IsNot_ExcludesMatch() =>
        Filter("CreatedOn", "IS NOT", "2024-06-15").Should().HaveCount(3);

    [Fact]
    public void DateTime_IsAfter_ReturnsDatesAfter() =>
        Filter("CreatedOn", "IS AFTER", "2024-06-15").Should().ContainSingle(p => p.Name == "Widget Pro");

    [Fact]
    public void DateTime_IsOnOrAfter_IncludesExactDate() =>
        Filter("CreatedOn", "IS ON OR AFTER", "2024-06-15").Should().HaveCount(2);

    [Fact]
    public void DateTime_IsBefore_ReturnsDatesBeforeValue() =>
        Filter("CreatedOn", "IS BEFORE", "2024-06-15").Should().HaveCount(2);

    [Fact]
    public void DateTime_IsOnOrBefore_IncludesExactDate() =>
        Filter("CreatedOn", "IS ON OR BEFORE", "2024-06-15").Should().HaveCount(3);

    [Fact]
    public void DateTime_IsEmpty_ReturnsNullDates() =>
        Filter("DeletedOn", "IS EMPTY", string.Empty).Should().HaveCount(3);

    [Fact]
    public void DateTime_IsNotEmpty_ReturnsNonNullDates() =>
        Filter("DeletedOn", "IS NOT EMPTY", string.Empty).Should().ContainSingle();

    [Fact]
    public void DateTime_InvalidDate_ReturnsAll() =>
        Filter("CreatedOn", "IS", "not-a-date").Should().HaveCount(4);

    [Fact]
    public void DateTime_UnknownOp_ReturnsAll() =>
        Filter("CreatedOn", "BETWEEN", "2024-01-01").Should().HaveCount(4);

    // ── Multiple filters ──
    [Fact]
    public void ApplyFilters_MultipleFilters_AppliesAll()
    {
        var filters = new Dictionary<string, (string, string)>
        {
            ["Name"] = ("CONTAINS", "Widget"),
            ["IsActive"] = ("IS", "true")
        };

        QueryFilterService.ApplyFilters(Products(), filters, EmptyMap).Should().HaveCount(2);
    }

    // ── Unknown property ──
    [Fact]
    public void ApplyFilters_UnknownProperty_SkipsFilter() =>
        Filter("NonExistent", "EQUALS", "value").Should().HaveCount(4);

    // ── Empty filters ──
    [Fact]
    public void ApplyFilters_EmptyFilters_ReturnsAll()
    {
        Dictionary<string, (string, string)> filters = [];
        QueryFilterService.ApplyFilters(Products(), filters, EmptyMap).Should().HaveCount(4);
    }

    // ── Property map ──
    [Fact]
    public void ApplyFilters_WithPropertyMap_UsesMappedProperty()
    {
        var map = new Dictionary<string, string> { ["Name"] = "Name" };
        var filters = new Dictionary<string, (string, string)> { ["Name"] = ("EQUALS", "Widget") };

        QueryFilterService.ApplyFilters(Products(), filters, map).Should().ContainSingle();
    }

    // ── RegisterStrategy ──
    [Fact]
    public void RegisterStrategy_NullType_Throws() =>
        FluentActions.Invoking(() => QueryFilterService.RegisterStrategy(null!, new TestStrategy()))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void RegisterStrategy_NullStrategy_Throws() =>
        FluentActions.Invoking(() => QueryFilterService.RegisterStrategy(typeof(string), null!))
            .Should().Throw<ArgumentNullException>();

    private sealed class TestStrategy : IFilterStrategy
    {
        public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value) => query;
    }
}
