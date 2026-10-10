using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Application.Tests.Services.Filtering;

/// <summary>
/// Pins the operator vocabulary the server accepts from a MudBlazor data grid (grid-filter audit,
/// run 9, items C). MudBlazor 9.11 sends its NUMBER column operators as symbols
/// (<c>FilterOperator.Number</c>: <c>"="</c>, <c>"!="</c>, <c>"&gt;"</c>, <c>"&gt;="</c>, <c>"&lt;"</c>,
/// <c>"&lt;="</c>), while the numeric strategies only knew the word forms, so every number filter was
/// refused as an unsupported operator. The symbols must validate AND apply with the same semantics as
/// their word forms, for every numeric strategy (int, long, decimal), and the word forms must keep
/// working. The Common <see cref="Email"/> value object must filter like a string (CONTAINS, EQUALS),
/// so a grid's Email column on an entity that stores the value object works.
/// </summary>
public sealed class QueryFilterServiceGridOperatorTests
{
    private static readonly Dictionary<string, string> EmptyMap = [];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("S1144", "S1144:Unused private types or members should be removed", Justification = "Properties are used via reflection by QueryFilterService")]
    private sealed class Product
    {
        public int Price { get; set; }

        public int? NullablePrice { get; set; }

        public long Quantity { get; set; }

        public decimal Amount { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("S1144", "S1144:Unused private types or members should be removed", Justification = "Properties are used via reflection by QueryFilterService")]
    private sealed class Customer
    {
        public string Name { get; set; } = string.Empty;

        public Email Email { get; set; } = default!;
    }

    private static IQueryable<Product> Products() =>
        new List<Product>
        {
            new() { Price = 10, NullablePrice = 10, Quantity = 10, Amount = 10m },
            new() { Price = 25, NullablePrice = 25, Quantity = 25, Amount = 25m },
            new() { Price = 50, NullablePrice = 50, Quantity = 50, Amount = 50m },
            new() { Price = 75, NullablePrice = null, Quantity = 75, Amount = 75m },
        }.AsQueryable();

    private static IQueryable<Customer> Customers() =>
        new List<Customer>
        {
            new() { Name = "Ada", Email = Email.Create("ada@example.com").Value! },
            new() { Name = "Grace", Email = Email.Create("grace@example.org").Value! },
            new() { Name = "Alan", Email = Email.Create("alan@example.org").Value! },
        }.AsQueryable();

    private static Dictionary<string, (string Operator, string Value)> One(string property, string op, string value) =>
        new() { [property] = (op, value) };

    /// <summary>The numeric properties, one per numeric strategy (int, nullable int, long, decimal).</summary>
    public static TheoryData<string> NumericProperties => new()
    {
        nameof(Product.Price),
        nameof(Product.NullablePrice),
        nameof(Product.Quantity),
        nameof(Product.Amount),
    };

    public static TheoryData<string, string, string> NumericPropertyBySymbol()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var property in new[] { nameof(Product.Price), nameof(Product.NullablePrice), nameof(Product.Quantity), nameof(Product.Amount) })
        {
            data.Add(property, "=", "EQUALS");
            data.Add(property, "!=", "NOT EQUALS");
            data.Add(property, ">", "GREATER THAN");
            data.Add(property, ">=", "GREATER THAN OR EQUAL");
            data.Add(property, "<", "LESS THAN");
            data.Add(property, "<=", "LESS THAN OR EQUAL");
        }

        return data;
    }

    // == Number operators: the MudBlazor symbols ==
    [Theory]
    [MemberData(nameof(NumericPropertyBySymbol))]
    public void ValidateFilters_MudBlazorNumberSymbol_IsAccepted(string property, string symbol, string word)
    {
        var result = QueryFilterService.ValidateFilters<Product>(One(property, symbol, "25"), EmptyMap);

        result.IsSuccess.Should().BeTrue(
            $"the grid sends '{symbol}' for {word} on a number column, and refusing it makes every number filter a 400");
    }

    [Theory]
    [MemberData(nameof(NumericPropertyBySymbol))]
    public void ApplyFilters_MudBlazorNumberSymbol_MatchesItsWordForm(string property, string symbol, string word)
    {
        var bySymbol = QueryFilterService.ApplyFilters(Products(), One(property, symbol, "25"), EmptyMap).ToList();
        var byWord = QueryFilterService.ApplyFilters(Products(), One(property, word, "25"), EmptyMap).ToList();

        bySymbol.Should().Equal(byWord, $"'{symbol}' must mean exactly what '{word}' means");
        bySymbol.Count.Should().BeLessThan(Products().Count(), $"'{symbol}' 25 must narrow the set, not be ignored");
    }

    [Fact]
    public void ApplyFilters_GreaterThanOrEqualSymbolOnAnInt_ReturnsTheExpectedRows() =>
        QueryFilterService.ApplyFilters(Products(), One(nameof(Product.Price), ">=", "25"), EmptyMap)
            .Select(p => p.Price)
            .Should().Equal(25, 50, 75);

    [Fact]
    public void ApplyFilters_NotEqualSymbolOnALong_ExcludesTheValue() =>
        QueryFilterService.ApplyFilters(Products(), One(nameof(Product.Quantity), "!=", "25"), EmptyMap)
            .Select(p => p.Quantity)
            .Should().Equal(10L, 50L, 75L);

    [Fact]
    public void ApplyFilters_LessThanSymbolOnADecimal_ReturnsTheExpectedRows() =>
        QueryFilterService.ApplyFilters(Products(), One(nameof(Product.Amount), "<", "25"), EmptyMap)
            .Select(p => p.Amount)
            .Should().Equal(10m);

    // == Number operators: the word forms still work (regression guard) ==
    [Theory]
    [MemberData(nameof(NumericPropertyBySymbol))]
    public void ValidateFilters_WordForm_StillAccepted(string property, string symbol, string word)
    {
        _ = symbol;

        QueryFilterService.ValidateFilters<Product>(One(property, word, "25"), EmptyMap)
            .IsSuccess.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(NumericProperties))]
    public void ValidateFilters_MudBlazorIsEmptyOnANumber_StillAccepted(string property)
    {
        // FilterOperator.Number.Empty is "is empty", which already upper-cases onto IS EMPTY.
        var result = QueryFilterService.ValidateFilters<Product>(One(property, "is empty", string.Empty), EmptyMap);

        result.IsSuccess.Should().BeTrue();
    }

    // == The Email value object filters like a string ==
    [Fact]
    public void ValidateFilters_ContainsOnAnEmailValueObject_IsAccepted()
    {
        var result = QueryFilterService.ValidateFilters<Customer>(One(nameof(Customer.Email), "contains", "example.org"), EmptyMap);

        result.IsSuccess.Should().BeTrue(
            "the grid's Email column sends CONTAINS, and an Email value-object property must accept it like a string");
    }

    [Fact]
    public void ValidateFilters_EqualsOnAnEmailValueObject_IsAccepted() =>
        QueryFilterService.ValidateFilters<Customer>(One(nameof(Customer.Email), "EQUALS", "ada@example.com"), EmptyMap)
            .IsSuccess.Should().BeTrue();

    [Fact]
    public void ApplyFilters_ContainsOnAnEmailValueObject_MatchesTheAddressText() =>
        QueryFilterService.ApplyFilters(Customers(), One(nameof(Customer.Email), "CONTAINS", "example.org"), EmptyMap)
            .Select(c => c.Name)
            .Should().Equal("Grace", "Alan");

    [Fact]
    public void ApplyFilters_EqualsOnAnEmailValueObject_MatchesTheWholeAddress() =>
        QueryFilterService.ApplyFilters(Customers(), One(nameof(Customer.Email), "EQUALS", "ada@example.com"), EmptyMap)
            .Select(c => c.Name)
            .Should().Equal("Ada");
}
