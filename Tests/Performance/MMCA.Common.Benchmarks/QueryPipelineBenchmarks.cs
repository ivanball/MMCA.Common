using System.Globalization;
using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Benchmarks;

/// <summary>
/// Allocation and latency coverage for the per-request query pipeline (rubric section 12). Filtering
/// and shaping run on every list read in every consumer, and both are dominated by work that is easy
/// to regress silently: the dynamic-LINQ predicate is re-parsed on each call, and the shaper reflects
/// over the DTO's properties. The specification suite next door covers the domain side; this covers
/// the read side.
/// </summary>
[MemoryDiagnoser]
public class QueryPipelineBenchmarks
{
    public sealed class ProductRow
    {
        public int Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Sku { get; init; } = string.Empty;

        public int Price { get; init; }

        public bool IsActive { get; init; }

        public DateTime CreatedOn { get; init; }
    }

    private static readonly Dictionary<string, string> NoMap = [];

    private readonly IQueryable<ProductRow> _rows = Enumerable.Range(0, 100)
        .Select(i => new ProductRow
        {
            Id = i,
            Name = $"Product {i.ToString(CultureInfo.InvariantCulture)}",
            Sku = $"SKU-{i.ToString("D4", CultureInfo.InvariantCulture)}",
            Price = i * 3,
            IsActive = i % 2 == 0,
            CreatedOn = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
        })
        .AsQueryable();

    private readonly List<ProductRow> _materialized;

    public QueryPipelineBenchmarks() => _materialized = [.. _rows];

    /// <summary>Single text filter: the shape behind every grid search box.</summary>
    [Benchmark]
    public Expression ApplyFilters_SingleStringContains() =>
        QueryFilterService.ApplyFilters(
            _rows,
            new Dictionary<string, (string, string)> { ["Name"] = ("CONTAINS", "Product 4") },
            NoMap).Expression;

    /// <summary>Three filters across three strategies: a realistic filtered grid request.</summary>
    [Benchmark]
    public Expression ApplyFilters_ThreeMixedOperators() =>
        QueryFilterService.ApplyFilters(
            _rows,
            new Dictionary<string, (string, string)>
            {
                ["Name"] = ("CONTAINS", "Product"),
                ["Price"] = ("GREATER THAN", "50"),
                ["IsActive"] = ("IS", "true"),
            },
            NoMap).Expression;

    /// <summary>Dynamic sort application, run once per paged read.</summary>
    [Benchmark]
    public Expression ApplySorting_Descending() =>
        QueryFieldService.ApplySorting(_rows, "Name", "desc", NoMap).Expression;

    /// <summary>Full-field shaping of one page: no field list, so every property is projected.</summary>
    [Benchmark]
    public int ShapeCollectionData_AllFields_100Rows() =>
        QueryFieldService.ShapeCollectionData(_materialized, fields: null).Count;

    /// <summary>Sparse-field shaping of one page: the `fields=` projection callers are told to prefer.</summary>
    [Benchmark]
    public int ShapeCollectionData_ThreeFields_100Rows() =>
        QueryFieldService.ShapeCollectionData(_materialized, "id,name,price").Count;
}
