using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;

namespace MMCA.Common.Domain.Tests.Specifications;

public sealed class SpecificationAdditionalTests
{
    private sealed class TestEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    private sealed class NameEqualsSpec(string name) : Specification<TestEntity, int>
    {
        public override Expression<Func<TestEntity, bool>> Criteria =>
            e => e.Name == name;
    }

    private sealed class AgeRangeSpec(int min, int max) : Specification<TestEntity, int>
    {
        public override Expression<Func<TestEntity, bool>> Criteria =>
            e => e.Age >= min && e.Age <= max;
    }

    // ── Criteria can be used as IQueryable filter ──
    [Fact]
    public void Criteria_CanFilterIQueryable()
    {
        var spec = new NameEqualsSpec("Alice");
        var data = new List<TestEntity>
        {
            new() { Id = 1, Name = "Alice", Age = 25 },
            new() { Id = 2, Name = "Bob", Age = 30 },
            new() { Id = 3, Name = "Alice", Age = 35 }
        }.AsQueryable();

        var results = data.Where(spec.Criteria).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(e => e.Name == "Alice");
    }

    // ── IsSatisfiedBy caches compiled delegate ──
    [Fact]
    public void IsSatisfiedBy_CalledMultipleTimes_CachesCompiledDelegate()
    {
        var spec = new NameEqualsSpec("Test");
        var entity = new TestEntity { Id = 1, Name = "Test" };

        // Call multiple times -- should not throw or produce different results
        spec.IsSatisfiedBy(entity).Should().BeTrue();
        spec.IsSatisfiedBy(entity).Should().BeTrue();
        spec.IsSatisfiedBy(new TestEntity { Id = 2, Name = "Other" }).Should().BeFalse();
    }

    // ── AndSpecification Criteria can filter IQueryable ──
    [Fact]
    public void AndSpecification_Criteria_FiltersQueryable()
    {
        var nameSpec = new NameEqualsSpec("Alice");
        var ageSpec = new AgeRangeSpec(20, 30);
        var andSpec = new AndSpecification<TestEntity, int>(nameSpec, ageSpec);

        var data = new List<TestEntity>
        {
            new() { Id = 1, Name = "Alice", Age = 25 },
            new() { Id = 2, Name = "Alice", Age = 35 },
            new() { Id = 3, Name = "Bob", Age = 25 }
        }.AsQueryable();

        var results = data.Where(andSpec.Criteria).ToList();

        results.Should().ContainSingle()
            .Which.Id.Should().Be(1);
    }

    // ── OrSpecification Criteria can filter IQueryable ──
    [Fact]
    public void OrSpecification_Criteria_FiltersQueryable()
    {
        var nameSpec = new NameEqualsSpec("Alice");
        var ageSpec = new AgeRangeSpec(30, 40);
        var orSpec = new OrSpecification<TestEntity, int>(nameSpec, ageSpec);

        var data = new List<TestEntity>
        {
            new() { Id = 1, Name = "Alice", Age = 25 },
            new() { Id = 2, Name = "Bob", Age = 35 },
            new() { Id = 3, Name = "Charlie", Age = 10 }
        }.AsQueryable();

        var results = data.Where(orSpec.Criteria).ToList();

        results.Should().HaveCount(2);
    }

    // ── NotSpecification Criteria can filter IQueryable ──
    [Fact]
    public void NotSpecification_Criteria_FiltersQueryable()
    {
        var nameSpec = new NameEqualsSpec("Alice");
        var notSpec = new NotSpecification<TestEntity, int>(nameSpec);

        var data = new List<TestEntity>
        {
            new() { Id = 1, Name = "Alice", Age = 25 },
            new() { Id = 2, Name = "Bob", Age = 30 }
        }.AsQueryable();

        var results = data.Where(notSpec.Criteria).ToList();

        results.Should().ContainSingle()
            .Which.Name.Should().Be("Bob");
    }

    // ── Complex composition: (A AND B) OR (NOT C) ──
    [Fact]
    public void ComplexComposition_CriteriaFiltersCorrectly()
    {
        var nameSpec = new NameEqualsSpec("Alice");
        var ageSpec = new AgeRangeSpec(20, 30);
        var andSpec = new AndSpecification<TestEntity, int>(nameSpec, ageSpec);
        var notNameSpec = new NotSpecification<TestEntity, int>(new NameEqualsSpec("Charlie"));
        var orSpec = new OrSpecification<TestEntity, int>(andSpec, notNameSpec);

        var data = new List<TestEntity>
        {
            new() { Id = 1, Name = "Alice", Age = 25 },     // AND true, NOT true -> true
            new() { Id = 2, Name = "Charlie", Age = 10 },   // AND false, NOT false -> false
            new() { Id = 3, Name = "Bob", Age = 35 },       // AND false, NOT true -> true
        }.AsQueryable();

        var results = data.Where(orSpec.Criteria).ToList();

        results.Should().HaveCount(2);
        results.Select(e => e.Name).Should().Contain("Alice").And.Contain("Bob");
    }
}
