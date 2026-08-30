using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class QueryFilterServiceValidateTests
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("S1144", "S1144:Unused private types or members should be removed", Justification = "Properties are used via reflection by QueryFilterService")]
    private sealed class Product
    {
        public string Name { get; set; } = string.Empty;
        public int Price { get; set; }
        public long Quantity { get; set; }
        public decimal Amount { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedOn { get; set; }
        public Category Category { get; set; } = new();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("S1144", "S1144:Unused private types or members should be removed", Justification = "Properties are used via reflection by QueryFilterService")]
    private sealed class Category
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private static readonly Dictionary<string, string> EmptyMap = [];

    // ── Null / empty filters ──
    [Fact]
    public void ValidateFilters_NullFilters_ReturnsSuccess()
    {
        var result = QueryFilterService.ValidateFilters<Product>(null, EmptyMap);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ValidateFilters_EmptyFilters_ReturnsSuccess()
    {
        Dictionary<string, (string, string)> filters = [];

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsSuccess.Should().BeTrue();
    }

    // ── Valid filters ──
    [Fact]
    public void ValidateFilters_ValidStringFilter_ReturnsSuccess()
    {
        var filters = new Dictionary<string, (string, string)>
        {
            ["Name"] = ("CONTAINS", "Widget")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ValidateFilters_ValidIntFilter_ReturnsSuccess()
    {
        var filters = new Dictionary<string, (string, string)>
        {
            ["Price"] = ("EQUALS", "10")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ValidateFilters_ValidDecimalFilter_ReturnsSuccess()
    {
        var filters = new Dictionary<string, (string, string)>
        {
            ["Amount"] = ("GREATER THAN", "9.99")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsSuccess.Should().BeTrue();
    }

    // ── Unknown property ──
    [Fact]
    public void ValidateFilters_UnknownProperty_ReturnsFailure()
    {
        var filters = new Dictionary<string, (string, string)>
        {
            ["NonExistent"] = ("EQUALS", "value")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Property.NotFound");
    }

    // ── Unsupported operator ──
    [Fact]
    public void ValidateFilters_UnsupportedIntOperator_ReturnsFailure()
    {
        var filters = new Dictionary<string, (string, string)>
        {
            ["Price"] = ("CONTAINS", "10")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Operator.NotSupported");
    }

    [Fact]
    public void ValidateFilters_UnsupportedDecimalOperator_ReturnsFailure()
    {
        var filters = new Dictionary<string, (string, string)>
        {
            ["Amount"] = ("CONTAINS", "9.99")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Operator.NotSupported");
    }

    [Fact]
    public void ValidateFilters_UnsupportedStringOperator_ReturnsFailure()
    {
        var filters = new Dictionary<string, (string, string)>
        {
            ["Name"] = ("GREATER THAN", "Widget")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Operator.NotSupported");
    }

    // ── Multiple errors ──
    [Fact]
    public void ValidateFilters_MultipleInvalidFilters_ReturnsAllErrors()
    {
        var filters = new Dictionary<string, (string, string)>
        {
            ["NonExistent"] = ("EQUALS", "value"),
            ["Price"] = ("CONTAINS", "10")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().HaveCount(2);
    }

    // ── Property map ──
    [Fact]
    public void ValidateFilters_WithPropertyMap_ResolvesMapping()
    {
        var map = new Dictionary<string, string> { ["Name"] = "Name" };
        var filters = new Dictionary<string, (string, string)>
        {
            ["Name"] = ("EQUALS", "Widget")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, map);

        result.IsSuccess.Should().BeTrue();
    }

    // ── Nested property (dot notation) validates against the LEAF's type ──
    [Fact]
    public void ValidateFilters_NestedStringLeaf_ValidatesWithStringStrategy()
    {
        var map = new Dictionary<string, string> { ["CategoryName"] = "Category.Name" };
        var filters = new Dictionary<string, (string, string)>
        {
            ["CategoryName"] = ("CONTAINS", "Electronics")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, map);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ValidateFilters_NestedStringLeaf_UnsupportedOperator_ReturnsFailure()
    {
        var map = new Dictionary<string, string> { ["CategoryName"] = "Category.Name" };
        var filters = new Dictionary<string, (string, string)>
        {
            ["CategoryName"] = ("GREATER THAN", "Electronics")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, map);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Operator.NotSupported");
    }

    [Fact]
    public void ValidateFilters_NestedNonStringLeaf_ValidatesWithTheLeafsStrategy()
    {
        // "Category.Id" is a Guid, so a string-only operator is rejected here rather than blowing up
        // inside Dynamic LINQ at query-build time.
        var map = new Dictionary<string, string> { ["CategoryId"] = "Category.Id" };
        var filters = new Dictionary<string, (string, string)>
        {
            ["CategoryId"] = ("CONTAINS", "Electronics")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, map);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Operator.NotSupported");
    }

    // ── Fail closed: a nested path whose leaf cannot be reached is an unknown property ──
    [Fact]
    public void ValidateFilters_NestedPathWithMissingLeaf_ReturnsPropertyNotFound()
    {
        var map = new Dictionary<string, string> { ["CategoryTitle"] = "Category.Title" };
        var filters = new Dictionary<string, (string, string)>
        {
            ["CategoryTitle"] = ("CONTAINS", "Electronics")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, map);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Property.NotFound");
    }

    [Fact]
    public void ValidateFilters_NestedPathWithMissingRoot_ReturnsPropertyNotFound()
    {
        // The DTO-facing name resolves, so the filter reaches type resolution; the path's own root
        // does not exist, and that must fail rather than fall back to the string strategy.
        var map = new Dictionary<string, string> { ["CreatedOn"] = "Supplier.Name" };
        var filters = new Dictionary<string, (string, string)>
        {
            ["CreatedOn"] = ("CONTAINS", "Acme")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, map);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Property.NotFound");
    }

    [Fact]
    public void ValidateFilters_NestedPathThroughAScalarSegment_ReturnsPropertyNotFound()
    {
        // "Price" is an int, so "Price.Amount" has nowhere to walk to.
        var map = new Dictionary<string, string> { ["PriceAmount"] = "Price.Amount" };
        var filters = new Dictionary<string, (string, string)>
        {
            ["PriceAmount"] = ("EQUALS", "10")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, map);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Property.NotFound");
    }

    // ── Unparseable values are rejected rather than silently widening the result set ──
    // A strategy that cannot parse a value returns the query unfiltered, so
    // "?filter=price:equals:abc" used to return every row instead of no rows, with a 200.
    [Theory]
    [InlineData("Price", "EQUALS", "abc")]
    [InlineData("Price", "GREATER THAN", "not-a-number")]
    [InlineData("Price", "IN", "a,b,c")]
    [InlineData("Price", "BETWEEN", "1")]
    [InlineData("Price", "BETWEEN", "1,2,3")]
    [InlineData("Amount", "EQUALS", "twelve")]
    [InlineData("IsActive", "IS", "maybe")]
    [InlineData("CreatedOn", "IS", "not-a-date")]
    public void ValidateFilters_UnparseableValue_ReturnsFailure(string property, string op, string value)
    {
        var filters = new Dictionary<string, (string, string)> { [property] = (op, value) };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Value.Invalid");
    }

    [Theory]
    [InlineData("Price", "EQUALS", "42")]
    [InlineData("Price", "IN", "1,2,3")]
    [InlineData("Price", "BETWEEN", "1,10")]
    [InlineData("Amount", "EQUALS", "12.50")]
    [InlineData("IsActive", "IS", "true")]
    [InlineData("CreatedOn", "IS", "2026-07-24")]
    [InlineData("Name", "CONTAINS", "anything at all")]
    public void ValidateFilters_ParseableValue_ReturnsSuccess(string property, string op, string value)
    {
        var filters = new Dictionary<string, (string, string)> { [property] = (op, value) };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("IS EMPTY")]
    [InlineData("IS NOT EMPTY")]
    public void ValidateFilters_PresenceOperator_IgnoresTheValue(string op)
    {
        // Presence checks never read the value, so an arbitrary one must not be rejected.
        var filters = new Dictionary<string, (string, string)> { ["Price"] = (op, "ignored") };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsSuccess.Should().BeTrue();
    }

    // ── BETWEEN needs exactly two segments that both parse ──
    // Dropping unparseable and empty segments let a three-segment value validate as a two-bound
    // range, and the strategy then applied bounds the caller never asked for.
    [Theory]
    [InlineData("Price", "5,abc,10")]
    [InlineData("Price", "5,,10")]
    [InlineData("Price", "5,10,")]
    [InlineData("Price", ",5,10")]
    [InlineData("Quantity", "5,abc,10")]
    [InlineData("Quantity", "5,,10")]
    [InlineData("Amount", "5.5,abc,10.5")]
    [InlineData("Amount", "5.5,,10.5")]
    [InlineData("CreatedOn", "2026-01-01,nope,2026-12-31")]
    [InlineData("CreatedOn", "2026-01-01,,2026-12-31")]
    public void ValidateFilters_Between_WithAnythingButTwoParseableSegments_ReturnsFailure(
        string property, string value)
    {
        var filters = new Dictionary<string, (string, string)> { [property] = ("BETWEEN", value) };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Value.Invalid");
    }

    [Theory]
    [InlineData("Price", "5,10")]
    [InlineData("Price", " 5 , 10 ")]
    [InlineData("Quantity", "5,10")]
    [InlineData("Amount", "5.5,10.5")]
    [InlineData("CreatedOn", "2026-01-01,2026-12-31")]
    public void ValidateFilters_Between_WithTwoValidBounds_ReturnsSuccess(string property, string value)
    {
        var filters = new Dictionary<string, (string, string)> { [property] = ("BETWEEN", value) };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ValidateFilters_UnsupportedOperator_ReportsOnlyTheOperatorError()
    {
        // One mistake should not produce two errors describing it.
        var filters = new Dictionary<string, (string, string)> { ["Price"] = ("CONTAINS", "abc") };

        var result = QueryFilterService.ValidateFilters<Product>(filters, EmptyMap);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Filter.Operator.NotSupported");
    }
}
