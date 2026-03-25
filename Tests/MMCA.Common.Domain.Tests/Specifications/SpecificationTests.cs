using System.Linq.Expressions;
using FluentAssertions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;

namespace MMCA.Common.Domain.Tests.Specifications;

public class SpecificationTests
{
    private sealed class TestEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    private sealed class NameStartsWithSpec(string prefix) : Specification<TestEntity, int>
    {
        public override Expression<Func<TestEntity, bool>> Criteria =>
            e => e.Name.StartsWith(prefix);
    }

    private sealed class AgeGreaterThanSpec(int threshold) : Specification<TestEntity, int>
    {
        public override Expression<Func<TestEntity, bool>> Criteria =>
            e => e.Age > threshold;
    }

    // ── IsSatisfiedBy ──
    [Fact]
    public void IsSatisfiedBy_MatchingEntity_ReturnsTrue()
    {
        var spec = new NameStartsWithSpec("A");
        var entity = new TestEntity { Id = 1, Name = "Alice" };

        spec.IsSatisfiedBy(entity).Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_NonMatchingEntity_ReturnsFalse()
    {
        var spec = new NameStartsWithSpec("B");
        var entity = new TestEntity { Id = 1, Name = "Alice" };

        spec.IsSatisfiedBy(entity).Should().BeFalse();
    }

    // ── AndSpecification ──
    [Fact]
    public void AndSpecification_BothTrue_ReturnsTrue()
    {
        var nameSpec = new NameStartsWithSpec("A");
        var ageSpec = new AgeGreaterThanSpec(18);
        var andSpec = new AndSpecification<TestEntity, int>(nameSpec, ageSpec);

        var entity = new TestEntity { Id = 1, Name = "Alice", Age = 25 };

        andSpec.IsSatisfiedBy(entity).Should().BeTrue();
    }

    [Fact]
    public void AndSpecification_OneFalse_ReturnsFalse()
    {
        var nameSpec = new NameStartsWithSpec("A");
        var ageSpec = new AgeGreaterThanSpec(30);
        var andSpec = new AndSpecification<TestEntity, int>(nameSpec, ageSpec);

        var entity = new TestEntity { Id = 1, Name = "Alice", Age = 25 };

        andSpec.IsSatisfiedBy(entity).Should().BeFalse();
    }

    // ── OrSpecification ──
    [Fact]
    public void OrSpecification_OneTrue_ReturnsTrue()
    {
        var nameSpec = new NameStartsWithSpec("B");
        var ageSpec = new AgeGreaterThanSpec(18);
        var orSpec = new OrSpecification<TestEntity, int>(nameSpec, ageSpec);

        var entity = new TestEntity { Id = 1, Name = "Alice", Age = 25 };

        orSpec.IsSatisfiedBy(entity).Should().BeTrue();
    }

    [Fact]
    public void OrSpecification_BothFalse_ReturnsFalse()
    {
        var nameSpec = new NameStartsWithSpec("B");
        var ageSpec = new AgeGreaterThanSpec(30);
        var orSpec = new OrSpecification<TestEntity, int>(nameSpec, ageSpec);

        var entity = new TestEntity { Id = 1, Name = "Alice", Age = 25 };

        orSpec.IsSatisfiedBy(entity).Should().BeFalse();
    }

    // ── NotSpecification ──
    [Fact]
    public void NotSpecification_NegatesTrue_ReturnsFalse()
    {
        var nameSpec = new NameStartsWithSpec("A");
        var notSpec = new NotSpecification<TestEntity, int>(nameSpec);

        var entity = new TestEntity { Id = 1, Name = "Alice" };

        notSpec.IsSatisfiedBy(entity).Should().BeFalse();
    }

    [Fact]
    public void NotSpecification_NegatesFalse_ReturnsTrue()
    {
        var nameSpec = new NameStartsWithSpec("B");
        var notSpec = new NotSpecification<TestEntity, int>(nameSpec);

        var entity = new TestEntity { Id = 1, Name = "Alice" };

        notSpec.IsSatisfiedBy(entity).Should().BeTrue();
    }

    // ── Composition ──
    [Fact]
    public void Composition_AndOrNot_WorkTogether()
    {
        var nameSpec = new NameStartsWithSpec("A");
        var ageSpec = new AgeGreaterThanSpec(18);
        var notAgeSpec = new NotSpecification<TestEntity, int>(ageSpec);
        var orSpec = new OrSpecification<TestEntity, int>(nameSpec, notAgeSpec);

        // Name starts with A, so OR is true even though age > 18 (notAge is false)
        var entity = new TestEntity { Id = 1, Name = "Alice", Age = 25 };
        orSpec.IsSatisfiedBy(entity).Should().BeTrue();
    }
}
