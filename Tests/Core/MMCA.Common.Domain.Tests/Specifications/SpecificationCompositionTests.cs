using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;

namespace MMCA.Common.Domain.Tests.Specifications;

/// <summary>
/// Pins HOW the boolean composers build their criteria, not just what the composed predicate
/// answers. Two properties matter and neither is visible from <c>IsSatisfiedBy</c>:
/// <list type="bullet">
///   <item>the composed tree contains no <see cref="InvocationExpression"/>, because a provider that
///   cannot unwrap one (Cosmos) throws at translation time on an ANDed specification;</item>
///   <item>the composed tree is built once per instance, because the query pipeline reads
///   <c>Criteria</c> on every request and the old implementation rebuilt it every time.</item>
/// </list>
/// </summary>
public sealed class SpecificationCompositionTests
{
    private sealed class CompositionTestEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public int Age { get; set; }
    }

    private sealed class NameStartsWithSpecification(string prefix) : Specification<CompositionTestEntity, int>
    {
        public override Expression<Func<CompositionTestEntity, bool>> Criteria =>
            e => e.Name.StartsWith(prefix, StringComparison.Ordinal);
    }

    private sealed class AgeGreaterThanSpecification(int threshold) : Specification<CompositionTestEntity, int>
    {
        public override Expression<Func<CompositionTestEntity, bool>> Criteria =>
            e => e.Age > threshold;
    }

    private static readonly CompositionTestEntity Alice = new() { Id = 1, Name = "Alice", Age = 25 };

    // ── No Expression.Invoke survives composition ──
    [Fact]
    public void AndSpecification_Criteria_ContainsNoInvocation()
    {
        var spec = new AndSpecification<CompositionTestEntity, int>(
            new NameStartsWithSpecification("A"),
            new AgeGreaterThanSpecification(18));

        InvocationFinder.Count(spec.Criteria).Should().Be(
            0,
            "an InvocationExpression in the criteria is what a non-relational provider refuses to translate");
    }

    [Fact]
    public void OrSpecification_Criteria_ContainsNoInvocation()
    {
        var spec = new OrSpecification<CompositionTestEntity, int>(
            new NameStartsWithSpecification("A"),
            new AgeGreaterThanSpecification(18));

        InvocationFinder.Count(spec.Criteria).Should().Be(0);
    }

    [Fact]
    public void NotSpecification_Criteria_ContainsNoInvocation()
    {
        var spec = new NotSpecification<CompositionTestEntity, int>(new NameStartsWithSpecification("A"));

        InvocationFinder.Count(spec.Criteria).Should().Be(0);
    }

    [Fact]
    public void NestedComposition_Criteria_ContainsNoInvocation()
    {
        var spec = new AndSpecification<CompositionTestEntity, int>(
            new OrSpecification<CompositionTestEntity, int>(
                new NameStartsWithSpecification("A"),
                new AgeGreaterThanSpecification(30)),
            new NotSpecification<CompositionTestEntity, int>(new NameStartsWithSpecification("Z")));

        InvocationFinder.Count(spec.Criteria).Should().Be(0);
    }

    // ── One parameter, and it is the lambda's own ──
    [Fact]
    public void AndSpecification_Criteria_UsesASingleParameterForBothSides()
    {
        var spec = new AndSpecification<CompositionTestEntity, int>(
            new NameStartsWithSpecification("A"),
            new AgeGreaterThanSpecification(18));

        var criteria = spec.Criteria;
        var parameters = ParameterFinder.Distinct(criteria.Body);

        criteria.Parameters.Should().ContainSingle();
        parameters.Should().ContainSingle("the right-hand body must be rebound onto the left-hand parameter");
        parameters.Should().Contain(criteria.Parameters[0]);
    }

    // ── Caching ──
    [Fact]
    public void AndSpecification_Criteria_IsBuiltOncePerInstance()
    {
        var spec = new AndSpecification<CompositionTestEntity, int>(
            new NameStartsWithSpecification("A"),
            new AgeGreaterThanSpecification(18));

        spec.Criteria.Should().BeSameAs(spec.Criteria);
    }

    [Fact]
    public void OrSpecification_Criteria_IsBuiltOncePerInstance()
    {
        var spec = new OrSpecification<CompositionTestEntity, int>(
            new NameStartsWithSpecification("A"),
            new AgeGreaterThanSpecification(18));

        spec.Criteria.Should().BeSameAs(spec.Criteria);
    }

    [Fact]
    public void NotSpecification_Criteria_IsBuiltOncePerInstance()
    {
        var spec = new NotSpecification<CompositionTestEntity, int>(new NameStartsWithSpecification("A"));

        spec.Criteria.Should().BeSameAs(spec.Criteria);
    }

    [Fact]
    public void SeparateInstances_DoNotShareTheirComposedCriteria()
    {
        var first = new AndSpecification<CompositionTestEntity, int>(
            new NameStartsWithSpecification("A"),
            new AgeGreaterThanSpecification(18));
        var second = new AndSpecification<CompositionTestEntity, int>(
            new NameStartsWithSpecification("A"),
            new AgeGreaterThanSpecification(18));

        first.Criteria.Should().NotBeSameAs(second.Criteria);
    }

    // ── Semantics still hold, evaluated through the compiled tree ──
    [Theory]
    [InlineData("A", 18, true)]
    [InlineData("A", 30, false)]
    [InlineData("B", 18, false)]
    public void AndSpecification_MatchesBothSides(string prefix, int threshold, bool expected)
    {
        var spec = new AndSpecification<CompositionTestEntity, int>(
            new NameStartsWithSpecification(prefix),
            new AgeGreaterThanSpecification(threshold));

        spec.Criteria.Compile()(Alice).Should().Be(expected);
        spec.IsSatisfiedBy(Alice).Should().Be(expected);
    }

    [Theory]
    [InlineData("B", 18, true)]
    [InlineData("A", 30, true)]
    [InlineData("B", 30, false)]
    public void OrSpecification_MatchesEitherSide(string prefix, int threshold, bool expected)
    {
        var spec = new OrSpecification<CompositionTestEntity, int>(
            new NameStartsWithSpecification(prefix),
            new AgeGreaterThanSpecification(threshold));

        spec.Criteria.Compile()(Alice).Should().Be(expected);
    }

    [Theory]
    [InlineData("A", false)]
    [InlineData("B", true)]
    public void NotSpecification_NegatesTheInnerCriteria(string prefix, bool expected)
    {
        var spec = new NotSpecification<CompositionTestEntity, int>(new NameStartsWithSpecification(prefix));

        spec.Criteria.Compile()(Alice).Should().Be(expected);
    }

    [Fact]
    public void Composition_AppliesAgainstAQueryable()
    {
        var rows = new List<CompositionTestEntity>
        {
            new() { Id = 1, Name = "Alice", Age = 25 },
            new() { Id = 2, Name = "Bob", Age = 40 },
            new() { Id = 3, Name = "Anna", Age = 12 },
        }.AsQueryable();

        var spec = new AndSpecification<CompositionTestEntity, int>(
            new NameStartsWithSpecification("A"),
            new AgeGreaterThanSpecification(18));

        rows.Where(spec.Criteria).Select(e => e.Id).Should().Equal(1);
    }

    // ── Null guards ──
    [Fact]
    public void AndSpecification_WithNullLeftSide_ThrowsWhenComposed()
    {
        var spec = new AndSpecification<CompositionTestEntity, int>(null!, new AgeGreaterThanSpecification(1));

        var act = () => spec.Criteria;

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void NotSpecification_WithNullInnerSpecification_ThrowsWhenComposed()
    {
        var spec = new NotSpecification<CompositionTestEntity, int>(null!);

        var act = () => spec.Criteria;

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Fluent extensions ──
    [Fact]
    public void And_ReturnsAnAndSpecificationWithTheSameSemantics()
    {
        var spec = new NameStartsWithSpecification("A").And(new AgeGreaterThanSpecification(18));

        spec.Should().BeOfType<AndSpecification<CompositionTestEntity, int>>();
        spec.IsSatisfiedBy(Alice).Should().BeTrue();
        InvocationFinder.Count(spec.Criteria).Should().Be(0);
    }

    [Fact]
    public void Or_ReturnsAnOrSpecificationWithTheSameSemantics()
    {
        var spec = new NameStartsWithSpecification("Z").Or(new AgeGreaterThanSpecification(18));

        spec.Should().BeOfType<OrSpecification<CompositionTestEntity, int>>();
        spec.IsSatisfiedBy(Alice).Should().BeTrue();
    }

    [Fact]
    public void Not_ReturnsANotSpecificationWithTheSameSemantics()
    {
        var spec = new NameStartsWithSpecification("A").Not();

        spec.Should().BeOfType<NotSpecification<CompositionTestEntity, int>>();
        spec.IsSatisfiedBy(Alice).Should().BeFalse();
    }

    [Fact]
    public void FluentChain_ComposesLeftToRight()
    {
        var spec = new NameStartsWithSpecification("A")
            .And(new AgeGreaterThanSpecification(30).Not());

        spec.IsSatisfiedBy(Alice).Should().BeTrue("Alice starts with A and is not over 30");
        InvocationFinder.Count(spec.Criteria).Should().Be(0);
    }

    [Fact]
    public void And_WithNullOther_Throws()
    {
        var spec = new NameStartsWithSpecification("A");

        var act = () => spec.And(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Or_WithNullOther_Throws()
    {
        var spec = new NameStartsWithSpecification("A");

        var act = () => spec.Or(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Counts the <see cref="InvocationExpression"/> nodes in an expression tree.</summary>
    private sealed class InvocationFinder : ExpressionVisitor
    {
        private int _count;

        public static int Count(Expression expression)
        {
            var finder = new InvocationFinder();
            finder.Visit(expression);
            return finder._count;
        }

        protected override Expression VisitInvocation(InvocationExpression node)
        {
            _count++;
            return base.VisitInvocation(node);
        }
    }

    /// <summary>Collects the distinct parameter instances referenced by an expression tree.</summary>
    private sealed class ParameterFinder : ExpressionVisitor
    {
        private readonly HashSet<ParameterExpression> _parameters = [];

        public static HashSet<ParameterExpression> Distinct(Expression expression)
        {
            var finder = new ParameterFinder();
            finder.Visit(expression);
            return finder._parameters;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _parameters.Add(node);
            return base.VisitParameter(node);
        }
    }
}
