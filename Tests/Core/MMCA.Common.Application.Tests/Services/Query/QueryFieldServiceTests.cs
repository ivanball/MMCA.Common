using AwesomeAssertions;
using MMCA.Common.Application.Services;

namespace MMCA.Common.Application.Tests.Services.Query;

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

    // ── ApplySorting: allowlist behavior ──
    [Fact]
    public void ApplySorting_UnmappedRealProperty_IsCaseInsensitive()
    {
        var query = new List<ProductDto>
        {
            new() { Id = 2, Name = "B" },
            new() { Id = 1, Name = "A" },
        }.AsQueryable();

        var sorted = QueryFieldService.ApplySorting(query, "name", "asc", new Dictionary<string, string>());

        sorted.First().Name.Should().Be("A");
    }

    [Fact]
    public void ApplySorting_UnmappedNonProperty_FallsBackToDefaultSort()
    {
        var query = new List<ProductDto>
        {
            new() { Id = 2, Name = "B" },
            new() { Id = 1, Name = "A" },
        }.AsQueryable();

        // "Category.Name" is a navigation path the DTO does not expose: it must never reach
        // Dynamic LINQ; the default sort applies instead.
        var sorted = QueryFieldService.ApplySorting(
            query, "Category.Name", "asc", new Dictionary<string, string>(), defaultSort: p => p.Id);

        sorted.Select(p => p.Id).Should().Equal(1, 2);
    }

    [Fact]
    public void ApplySorting_ExpressionString_DoesNotThrow_ReturnsUnsortedWithoutDefault()
    {
        var query = new List<ProductDto>
        {
            new() { Id = 2, Name = "B" },
            new() { Id = 1, Name = "A" },
        }.AsQueryable();

        // A client-supplied expression must not reach Dynamic LINQ (no parse-error 500s).
        var act = () => QueryFieldService.ApplySorting(
            query, "(Id + Price)", "asc", new Dictionary<string, string>()).ToList();

        act.Should().NotThrow().Which.Select(p => p.Id).Should().Equal(2, 1);
    }

    [Fact]
    public void ApplySorting_MappedName_WinsOverPropertyLookup()
    {
        var query = new List<ProductDto>
        {
            new() { Id = 1, Name = "C", Price = 3m },
            new() { Id = 2, Name = "A", Price = 1m },
        }.AsQueryable();

        // The map redirects the DTO's "Name" to the entity's Price: the mapped target must be
        // used even though "Name" also names a real property.
        var map = new Dictionary<string, string> { ["Name"] = "Price" };

        var sorted = QueryFieldService.ApplySorting(query, "Name", "desc", map);

        sorted.First().Price.Should().Be(3m);
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

    // ── Validate: DTO-to-entity map overload ──
    // The map is server-authored, so a mapped name is accepted without resolving its VALUE: mapped
    // values are navigation paths or Dynamic LINQ expressions, not necessarily property names.
    public sealed class MappedDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Display => Name.ToUpperInvariant();
    }

    private static Dictionary<string, string> Map(params (string DtoName, string EntityPath)[] entries)
        => entries.ToDictionary(e => e.DtoName, e => e.EntityPath, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Validate_WithMap_MappedNestedPath_ReturnsSuccess()
    {
        // "CategoryName" is not a property of MappedDto at all; only the map makes it valid.
        var result = QueryFieldService.Validate<MappedDto>(
            "CategoryName",
            Map(("CategoryName", "Category.Name")),
            allowWriteableFields: true);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithMap_MappedExpressionValue_ReturnsSuccess()
    {
        // A mapped value that is an expression rather than a property path is still accepted:
        // the value is never resolved or reflected over.
        var result = QueryFieldService.Validate<MappedDto>(
            "Label",
            Map(("Label", "Id.ToString() + Name")),
            allowWriteableFields: true);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithMap_MappedReadOnlyProperty_IsAcceptedForShaping()
    {
        // A map hit short-circuits the read-only rule too: the server author decided this name
        // resolves to something usable, so the CanWrite check never runs.
        var result = QueryFieldService.Validate<MappedDto>(
            "Caption",
            Map(("Caption", "Display")),
            allowWriteableFields: false);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithMap_UnmappedBogusField_StillReturnsFailure()
    {
        var result = QueryFieldService.Validate<MappedDto>(
            "CategoryName,NotAField",
            Map(("CategoryName", "Category.Name")),
            allowWriteableFields: true);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("NotAField");
    }

    [Fact]
    public void Validate_WithMap_UnmappedReadOnlyProperty_StillRejectedForShaping()
    {
        var result = QueryFieldService.Validate<MappedDto>("Display", Map(), allowWriteableFields: false);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("read-only");
    }

    [Fact]
    public void Validate_WithEmptyMap_BehavesLikeTheMaplessOverload()
    {
        QueryFieldService.Validate<MappedDto>("Id,Name", Map()).IsSuccess.Should().BeTrue();
        QueryFieldService.Validate<MappedDto>("NonExistent", Map()).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithMap_MapLookupIsCaseInsensitiveWhenTheMapIs()
    {
        var result = QueryFieldService.Validate<MappedDto>(
            "categoryname",
            Map(("CategoryName", "Category.Name")),
            allowWriteableFields: true);

        result.IsSuccess.Should().BeTrue();
    }

    // ── Field-set cache caps ──
    // Both caches are keyed by a client-supplied field set, so they are capped. The assertions are
    // "<= cap", never "== cap": the caches are static and shared with every other test in the run.
    private const int MaxCacheEntries = 512;

    public sealed class CacheProbeEntity
    {
        public int P0 { get; set; }
        public int P1 { get; set; }
        public int P2 { get; set; }
        public int P3 { get; set; }
        public int P4 { get; set; }
        public int P5 { get; set; }
        public int P6 { get; set; }
        public int P7 { get; set; }
        public int P8 { get; set; }
        public int P9 { get; set; }
    }

    private static readonly string[] ProbeProperties =
        ["P0", "P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8", "P9"];

    /// <summary>Distinct, non-empty field sets built from the bits of <paramref name="index"/>.</summary>
    private static string FieldSet(int index)
        => string.Join(
            ',',
            Enumerable.Range(0, ProbeProperties.Length)
                .Where(bit => (index & (1 << bit)) != 0)
                .Select(bit => ProbeProperties[bit]));

    private static int CacheEntryCount(string cacheFieldName)
    {
        var cache = typeof(QueryFieldService)
            .GetField(cacheFieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        return (int)cache.GetType().GetProperty("Count")!.GetValue(cache)!;
    }

    [Fact]
    public void ApplyFieldSelection_ManyDistinctFieldSets_StopsGrowingTheProjectionCache()
    {
        var query = new List<CacheProbeEntity> { new() { P0 = 1, P3 = 3, P9 = 9 } }.AsQueryable();

        for (var i = 1; i <= 700; i++)
        {
            _ = QueryFieldService.ApplyFieldSelection(query, FieldSet(i)).ToList();
        }

        CacheEntryCount("ProjectionCache").Should().BeLessThanOrEqualTo(MaxCacheEntries);
    }

    [Fact]
    public void ApplyFieldSelection_PastTheCap_SkipsProjectionButStillReturnsCorrectRows()
    {
        var entity = new CacheProbeEntity { P0 = 1, P3 = 3, P9 = 9 };
        var query = new List<CacheProbeEntity> { entity }.AsQueryable();

        for (var i = 1; i <= 700; i++)
        {
            _ = QueryFieldService.ApplyFieldSelection(query, FieldSet(i)).ToList();
        }

        // "P0,P3,P9" (bits 0, 3 and 9) is set 521 of the enumeration above, so it was already past
        // the cap when the loop reached it: the cache holds no entry for it and admits none now.
        var entriesBefore = CacheEntryCount("ProjectionCache");
        var rows = QueryFieldService.ApplyFieldSelection(query, "P0,P3,P9").ToList();

        CacheEntryCount("ProjectionCache").Should().Be(entriesBefore);
        rows.Should().ContainSingle();
        rows[0].P0.Should().Be(1);
        rows[0].P3.Should().Be(3);
        rows[0].P9.Should().Be(9);
    }

    [Fact]
    public void ShapeData_ManyDistinctFieldSets_StopsGrowingTheShapedAccessorCache()
    {
        var entity = new CacheProbeEntity { P0 = 1, P3 = 3, P9 = 9 };

        for (var i = 1; i <= 700; i++)
        {
            QueryFieldService.ShapeData(entity, FieldSet(i));
        }

        CacheEntryCount("ShapedAccessorCache").Should().BeLessThanOrEqualTo(MaxCacheEntries);
    }

    [Fact]
    public void ShapeData_PastTheCap_StillShapesToTheRequestedFields()
    {
        var entity = new CacheProbeEntity { P0 = 1, P3 = 3, P9 = 9 };

        for (var i = 1; i <= 700; i++)
        {
            QueryFieldService.ShapeData(entity, FieldSet(i));
        }

        // Same reasoning as the projection test: this field set is past the cap, so the accessors
        // are filtered per request and nothing new is admitted.
        var entriesBefore = CacheEntryCount("ShapedAccessorCache");
        var dict = (IDictionary<string, object?>)QueryFieldService.ShapeData(entity, "P0,P3,P9");

        CacheEntryCount("ShapedAccessorCache").Should().Be(entriesBefore);
        dict.Should().ContainKeys("p0", "p3", "p9");
        dict.Should().NotContainKeys("p1", "p2");
        dict["p3"].Should().Be(3);
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
