using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.Application.Tests.Services.Query;

/// <summary>
/// The server half of the search-box fix (grid-filter audit, run 9, item B). When a list page's search
/// box maps onto a column the grid already filters, the page base keeps the column filter under its
/// own key and sends the search under the aliased key <c>Name~search</c>
/// (<see cref="QueryFilterKeys"/>). The generic query path (<see cref="QueryFilterService"/>, which
/// <c>EntityQueryPipeline</c> runs) must resolve the aliased key to the SAME property, through the
/// same DTO-to-entity map and the same response-contract gate as the bare key, and AND the two
/// predicates.
/// </summary>
public sealed class QueryFilterAliasedKeyTests
{
    private static readonly Dictionary<string, string> EmptyMap = [];

    private static readonly string SearchKey = QueryFilterKeys.Alias("Name", QueryFilterKeys.SearchTag);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("S1144", "S1144:Unused private types or members should be removed", Justification = "Properties are used via reflection by QueryFilterService")]
    private sealed class Product
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string InternalNote { get; set; } = string.Empty;
    }

    private static IQueryable<Product> Products() =>
        new List<Product>
        {
            new() { Id = 1, Name = "Blue Shirt", Title = "Blue Shirt", InternalNote = "blue" },
            new() { Id = 2, Name = "Blue Hat", Title = "Blue Hat", InternalNote = "blue" },
            new() { Id = 3, Name = "Red Shirt", Title = "Red Shirt", InternalNote = "red" },
        }.AsQueryable();

    [Fact]
    public void AliasedKey_NamesTheSameProperty() =>
        QueryFilterKeys.PropertyOf(SearchKey).Should().Be("Name");

    [Fact]
    public void ApplyFilters_ColumnFilterAndAliasedSearch_AndsBothOnTheSameProperty()
    {
        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = ("ends with", "Shirt"),
            [SearchKey] = ("contains", "Blue"),
        };

        QueryFilterService.ValidateFilters<Product>(filters, EmptyMap).IsSuccess.Should().BeTrue();
        QueryFilterService.ApplyFilters(Products(), filters, EmptyMap)
            .Select(p => p.Id)
            .Should().Equal([1], "only Blue Shirt both ends with 'Shirt' (the column) and contains 'Blue' (the search)");
    }

    [Fact]
    public void ApplyFilters_AliasedSearchAlone_FiltersLikeTheBareKey()
    {
        var aliased = new Dictionary<string, (string Operator, string Value)> { [SearchKey] = ("contains", "Shirt") };
        var bare = new Dictionary<string, (string Operator, string Value)> { ["Name"] = ("contains", "Shirt") };

        QueryFilterService.ApplyFilters(Products(), aliased, EmptyMap).Select(p => p.Id)
            .Should().Equal(QueryFilterService.ApplyFilters(Products(), bare, EmptyMap).Select(p => p.Id));
    }

    [Fact]
    public void ApplyFilters_AliasedKey_ResolvesThroughTheDtoToEntityMap()
    {
        // A page whose DTO field is renamed on the entity ("Name" -> "Title") keeps working for the
        // aliased search key: the map entry for the bare key applies to it too.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Name"] = "Title" };
        var filters = new Dictionary<string, (string Operator, string Value)>
        {
            ["Name"] = ("starts with", "Blue"),
            [SearchKey] = ("contains", "Hat"),
        };

        QueryFilterService.ValidateFilters<Product>(filters, map).IsSuccess.Should().BeTrue();
        QueryFilterService.ApplyFilters(Products(), filters, map).Select(p => p.Id).Should().Equal(2);
    }

    [Fact]
    public void ValidateAndApply_AliasedKey_IsGatedByTheResponseContractLikeTheBareKey()
    {
        var contract = QueryFieldContract.ForNames(["Id", "Name"]);
        var allowed = new Dictionary<string, (string Operator, string Value)> { [SearchKey] = ("contains", "Red") };
        var refused = new Dictionary<string, (string Operator, string Value)>
        {
            [QueryFilterKeys.Alias("InternalNote", QueryFilterKeys.SearchTag)] = ("contains", "blue"),
        };

        QueryFilterService.ValidateFilters<Product>(allowed, EmptyMap, contract).IsSuccess.Should().BeTrue();
        QueryFilterService.ApplyFilters(Products(), allowed, EmptyMap, contract).Select(p => p.Id).Should().Equal(3);

        QueryFilterService.ValidateFilters<Product>(refused, EmptyMap, contract).IsFailure
            .Should().BeTrue("an alias must not reach a field the response contract does not declare");
        QueryFilterService.ApplyFilters(Products(), refused, EmptyMap, contract).Should().HaveCount(3);
    }
}
