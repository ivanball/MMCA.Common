using FluentAssertions;
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
}
