using FluentAssertions;
using MMCA.Common.Application.Services;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Tests.Services;

public class QueryFieldServiceTests
{
    private sealed class ProductDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }

    // ── ShapeData ──
    [Fact]
    public void ShapeData_WithNoFields_ReturnsAllProperties()
    {
        var dto = new ProductDto { Id = 1, Name = "Widget", Price = 9.99m };

        var shaped = QueryFieldService.ShapeData(dto, null);
        var dict = (IDictionary<string, object?>)shaped;

        dict.Should().ContainKeys("id", "name", "price");
    }

    [Fact]
    public void ShapeData_WithSpecificFields_ReturnsOnlyThoseFields()
    {
        var dto = new ProductDto { Id = 1, Name = "Widget", Price = 9.99m };

        var shaped = QueryFieldService.ShapeData(dto, "Id,Name");
        var dict = (IDictionary<string, object?>)shaped;

        dict.Should().ContainKeys("id", "name");
        dict.Should().NotContainKey("price");
    }

    [Fact]
    public void ShapeData_FieldsAreCaseInsensitive()
    {
        var dto = new ProductDto { Id = 1, Name = "Widget", Price = 9.99m };

        var shaped = QueryFieldService.ShapeData(dto, "id,name");
        var dict = (IDictionary<string, object?>)shaped;

        dict.Should().ContainKeys("id", "name");
    }

    // ── ShapeCollectionData ──
    [Fact]
    public void ShapeCollectionData_ReturnsShapedListForEachEntity()
    {
        var dtos = new[]
        {
            new ProductDto { Id = 1, Name = "A", Price = 1m },
            new ProductDto { Id = 2, Name = "B", Price = 2m },
        };

        var result = QueryFieldService.ShapeCollectionData(dtos, "Id,Name");

        result.Should().HaveCount(2);
        var dict = (IDictionary<string, object?>)result[0];
        dict.Should().ContainKeys("id", "name");
        dict.Should().NotContainKey("price");
    }

    [Fact]
    public void ShapeCollectionData_WithNoFields_ReturnsAllProperties()
    {
        var dtos = new[] { new ProductDto { Id = 1, Name = "A", Price = 1m } };

        var result = QueryFieldService.ShapeCollectionData(dtos, null);

        var dict = (IDictionary<string, object?>)result[0];
        dict.Should().ContainKeys("id", "name", "price");
    }

    // ── ApplySorting ──
    [Fact]
    public void ApplySorting_Ascending_SortsByColumn()
    {
        var query = new List<ProductDto>
        {
            new() { Id = 2, Name = "B" },
            new() { Id = 1, Name = "A" },
            new() { Id = 3, Name = "C" },
        }.AsQueryable();

        var sorted = QueryFieldService.ApplySorting(query, "Name", "asc", new Dictionary<string, string>());

        sorted.First().Name.Should().Be("A");
        sorted.Last().Name.Should().Be("C");
    }

    [Fact]
    public void ApplySorting_Descending_SortsByColumnDescending()
    {
        var query = new List<ProductDto>
        {
            new() { Id = 1, Name = "A" },
            new() { Id = 2, Name = "B" },
            new() { Id = 3, Name = "C" },
        }.AsQueryable();

        var sorted = QueryFieldService.ApplySorting(query, "Name", "desc", new Dictionary<string, string>());

        sorted.First().Name.Should().Be("C");
        sorted.Last().Name.Should().Be("A");
    }

    [Fact]
    public void ApplySorting_WithMapping_UsesMappedProperty()
    {
        var query = new List<ProductDto>
        {
            new() { Id = 2, Name = "B" },
            new() { Id = 1, Name = "A" },
        }.AsQueryable();

        var map = new Dictionary<string, string> { ["DisplayName"] = "Name" };

        var sorted = QueryFieldService.ApplySorting(query, "DisplayName", "asc", map);

        sorted.First().Name.Should().Be("A");
    }

    [Fact]
    public void ApplySorting_NullSortColumn_ReturnsOriginalQuery()
    {
        var query = new List<ProductDto>
        {
            new() { Id = 2, Name = "B" },
            new() { Id = 1, Name = "A" },
        }.AsQueryable();

        var sorted = QueryFieldService.ApplySorting(query, null, null, new Dictionary<string, string>());

        sorted.First().Id.Should().Be(2);
    }

    // ── Validate ──
    [Fact]
    public void Validate_NullFields_ReturnsSuccess() =>
        QueryFieldService.Validate<ProductDto>(null).IsSuccess.Should().BeTrue();

    [Fact]
    public void Validate_EmptyFields_ReturnsSuccess() =>
        QueryFieldService.Validate<ProductDto>(string.Empty).IsSuccess.Should().BeTrue();

    [Fact]
    public void Validate_ValidFields_ReturnsSuccess() =>
        QueryFieldService.Validate<ProductDto>("Id,Name").IsSuccess.Should().BeTrue();

    [Fact]
    public void Validate_InvalidField_ReturnsFailure()
    {
        var result = QueryFieldService.Validate<ProductDto>("NonExistent");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Error.InvalidEntityField");
    }

    [Fact]
    public void Validate_MixOfValidAndInvalid_ReturnsErrors()
    {
        var result = QueryFieldService.Validate<ProductDto>("Id,FakeField,AnotherFake");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().HaveCount(2);
    }

    // ── ValidateSortDirection ──
    [Fact]
    public void ValidateSortDirection_Null_ReturnsSuccess() =>
        QueryFieldService.ValidateSortDirection(null).IsSuccess.Should().BeTrue();

    [Fact]
    public void ValidateSortDirection_Asc_ReturnsSuccess() =>
        QueryFieldService.ValidateSortDirection("asc").IsSuccess.Should().BeTrue();

    [Fact]
    public void ValidateSortDirection_Desc_ReturnsSuccess() =>
        QueryFieldService.ValidateSortDirection("desc").IsSuccess.Should().BeTrue();

    [Fact]
    public void ValidateSortDirection_CaseInsensitive_ReturnsSuccess() =>
        QueryFieldService.ValidateSortDirection("ASC").IsSuccess.Should().BeTrue();

    [Fact]
    public void ValidateSortDirection_Invalid_ReturnsFailure()
    {
        var result = QueryFieldService.ValidateSortDirection("sideways");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Error.InvalidSortDirection");
    }
}
