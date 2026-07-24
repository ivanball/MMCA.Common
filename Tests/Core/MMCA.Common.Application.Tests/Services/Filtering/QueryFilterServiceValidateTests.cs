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
        public decimal Amount { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedOn { get; set; }
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

    // ── Nested property (dot notation) uses string filtering ──
    [Fact]
    public void ValidateFilters_NestedProperty_ValidatesWithStringStrategy()
    {
        var map = new Dictionary<string, string> { ["CreatedOn"] = "Category.Name" };
        var filters = new Dictionary<string, (string, string)>
        {
            ["CreatedOn"] = ("CONTAINS", "Electronics")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, map);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ValidateFilters_NestedProperty_UnsupportedOperator_ReturnsFailure()
    {
        var map = new Dictionary<string, string> { ["CreatedOn"] = "Category.Name" };
        var filters = new Dictionary<string, (string, string)>
        {
            ["CreatedOn"] = ("GREATER THAN", "Electronics")
        };

        var result = QueryFilterService.ValidateFilters<Product>(filters, map);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Filter.Operator.NotSupported");
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
