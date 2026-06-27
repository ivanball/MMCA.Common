using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;

namespace MMCA.Common.Benchmarks;

/// <summary>
/// Smoke benchmarks for the Specification hot path (rubric §12 — "measured, not assumed"): the
/// per-instance compiled-expression cache behind <see cref="Specification{TEntity, TId}.IsSatisfiedBy"/>
/// and the EF-translatable And/Or composition cost. DB-free and fast; run on demand (see csproj).
/// </summary>
[MemoryDiagnoser]
public class SpecificationBenchmarks
{
    private sealed class SampleItem : BaseEntity<int>
    {
        public int Value { get; init; }

        public bool Active { get; init; }
    }

    private sealed class MinValueSpec(int min) : Specification<SampleItem, int>
    {
        public override Expression<Func<SampleItem, bool>> Criteria => x => x.Value >= min;
    }

    private sealed class ActiveSpec : Specification<SampleItem, int>
    {
        public override Expression<Func<SampleItem, bool>> Criteria => x => x.Active;
    }

    private readonly SampleItem _item = new() { Id = 1, Value = 42, Active = true };
    private readonly MinValueSpec _cachedSpec = new(10);

    /// <summary>Production path: one spec instance reused, so the compiled expression is cached.</summary>
    [Benchmark(Baseline = true)]
    public bool IsSatisfiedBy_CachedCompile() => _cachedSpec.IsSatisfiedBy(_item);

    /// <summary>The anti-pattern the cache avoids: a fresh spec per evaluation recompiles each call.</summary>
    [Benchmark]
    public bool IsSatisfiedBy_RecompileEachCall() => new MinValueSpec(10).IsSatisfiedBy(_item);

    /// <summary>Composition cost: building an And(min, Or(active, min2)) EF-translatable criteria tree.</summary>
    [Benchmark]
    public Expression BuildComposedCriteria()
    {
        var composed = new AndSpecification<SampleItem, int>(
            new MinValueSpec(10),
            new OrSpecification<SampleItem, int>(new ActiveSpec(), new MinValueSpec(20)));
        return composed.Criteria;
    }
}
