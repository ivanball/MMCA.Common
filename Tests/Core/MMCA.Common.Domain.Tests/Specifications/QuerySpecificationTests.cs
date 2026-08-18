using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Specifications;

namespace MMCA.Common.Domain.Tests.Specifications;

/// <summary>
/// Covers the builder state a <see cref="QuerySpecification{TEntity, TIdentifierType}"/> carries
/// beyond its predicate: includes, ordering, paging, tracking, and soft-delete scope. It also pins
/// the base chain, which the <c>SpecificationsDoNotNavigateToOtherEntities</c> fitness rule keys on.
/// </summary>
public sealed class QuerySpecificationTests
{
    private sealed class QueryTestEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public int Age { get; set; }
    }

    private sealed class DefaultsSpecification : QuerySpecification<QueryTestEntity, int>
    {
        public override Expression<Func<QueryTestEntity, bool>> Criteria => e => e.Age > 0;
    }

    private sealed class FullyConfiguredSpecification : QuerySpecification<QueryTestEntity, int>
    {
        public FullyConfiguredSpecification()
        {
            AddInclude("Owner");
            AddInclude("Owner.Address");
            AddInclude("   ");
            AddInclude("Owner");
            AddOrderBy(e => e.Name);
            AddOrderBy(e => e.Age, descending: true);
            ApplyPaging(skip: 10, take: 25);
            WithTracking();
            WithSoftDeleted();
        }

        public override Expression<Func<QueryTestEntity, bool>> Criteria => e => e.Name != string.Empty;
    }

    private sealed class NegativePagingSpecification : QuerySpecification<QueryTestEntity, int>
    {
        public NegativePagingSpecification() => ApplyPaging(skip: -5, take: -3);

        public override Expression<Func<QueryTestEntity, bool>> Criteria => e => true;
    }

    // ── Defaults ──
    [Fact]
    public void Defaults_AreAnUnorderedUnpagedUntrackedFilteredRead()
    {
        var spec = new DefaultsSpecification();

        spec.OrderBy.Should().BeEmpty();
        spec.IncludePaths.Should().BeEmpty();
        spec.Skip.Should().BeNull();
        spec.Take.Should().BeNull();
        spec.AsTracking.Should().BeFalse();
        spec.IgnoreQueryFilters.Should().BeFalse();
    }

    [Fact]
    public void QuerySpecification_IsStillASpecification()
    {
        var spec = new DefaultsSpecification();

        spec.Should().BeAssignableTo<Specification<QueryTestEntity, int>>(
            "the fitness rule keys on the Specification base-type prefix");
        spec.Should().BeAssignableTo<ISpecification<QueryTestEntity, int>>();
        spec.IsSatisfiedBy(new QueryTestEntity { Id = 1, Age = 3 }).Should().BeTrue();
        spec.IsSatisfiedBy(new QueryTestEntity { Id = 2, Age = 0 }).Should().BeFalse();
    }

    // ── Builders ──
    [Fact]
    public void AddInclude_KeepsOrder_IgnoresBlanks_AndDoesNotDuplicate()
    {
        var spec = new FullyConfiguredSpecification();

        spec.IncludePaths.Should().Equal("Owner", "Owner.Address");
    }

    [Fact]
    public void AddOrderBy_RecordsEachKeyWithItsDirection()
    {
        var spec = new FullyConfiguredSpecification();

        spec.OrderBy.Should().HaveCount(2);
        spec.OrderBy[0].Descending.Should().BeFalse();
        spec.OrderBy[0].KeySelector.ReturnType.Should().Be<string>();
        spec.OrderBy[1].Descending.Should().BeTrue();
        spec.OrderBy[1].KeySelector.ReturnType.Should().Be<int>();
    }

    [Fact]
    public void ApplyPaging_RecordsTheWindow()
    {
        var spec = new FullyConfiguredSpecification();

        spec.Skip.Should().Be(10);
        spec.Take.Should().Be(25);
    }

    [Fact]
    public void ApplyPaging_FloorsNegativeValuesAtZero()
    {
        var spec = new NegativePagingSpecification();

        spec.Skip.Should().Be(0);
        spec.Take.Should().Be(0);
    }

    [Fact]
    public void WithTrackingAndWithSoftDeleted_FlipTheirFlags()
    {
        var spec = new FullyConfiguredSpecification();

        spec.AsTracking.Should().BeTrue();
        spec.IgnoreQueryFilters.Should().BeTrue();
    }

    [Fact]
    public void OrderByAndIncludePaths_AreReadOnlyToCallers()
    {
        var spec = new FullyConfiguredSpecification();

        spec.IncludePaths.Should().BeAssignableTo<IReadOnlyList<string>>();
        spec.OrderBy.Should().BeAssignableTo<IReadOnlyList<OrderExpression>>();
    }

    // ── Composition still works on a query specification ──
    [Fact]
    public void AQuerySpecification_ComposesLikeAnyOtherSpecification()
    {
        var spec = new DefaultsSpecification().And(new DefaultsSpecification().Not());

        spec.IsSatisfiedBy(new QueryTestEntity { Id = 1, Age = 3 }).Should().BeFalse(
            "a predicate ANDed with its own negation matches nothing");
    }
}
