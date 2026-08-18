using System.Linq.Expressions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Verifies the <c>SpecificationsDoNotNavigateToOtherEntities</c> fitness function: it flags a
/// specification whose Criteria navigates to another entity (unsafe across data sources) but not one
/// that filters only on the entity's own scalar columns.
/// </summary>
public sealed class SpecificationFitnessTests
{
    [Fact]
    public void Rule_FlagsNavigatingSpecification_ButNotScalarSpecification()
    {
        var act = () => ArchitectureRules.SpecificationsDoNotNavigateToOtherEntities(new SpecTestMap());

        var exception = act.Should().Throw<Exception>().Which;
        exception.Message.Should().Contain(nameof(NavigatingSpec), "the spec navigates to another entity");
        exception.Message.Should().Contain("navigates");
        exception.Message.Should().NotContain(nameof(ScalarOnlySpec), "scalar-only specs are safe across data sources");
    }

    [Fact]
    public void Rule_AlsoAnalyzesQuerySpecifications()
    {
        var act = () => ArchitectureRules.SpecificationsDoNotNavigateToOtherEntities(new SpecTestMap());

        var exception = act.Should().Throw<Exception>().Which;
        exception.Message.Should().Contain(
            nameof(NavigatingQuerySpec),
            "a QuerySpecification keeps the Specification base chain the rule keys on, so it stays analyzable");
        exception.Message.Should().NotContain(
            nameof(ScalarOnlyQuerySpec),
            "a query specification that filters on its own columns is as safe as any other");
    }

    private sealed class SpecTestMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Framework(Layer.Application, typeof(SpecificationFitnessTests).Assembly)];
    }

    public sealed class FitnessDependent : AuditableBaseEntity<int>
    {
        public int PrincipalId { get; set; }

        public FitnessPrincipal? Principal { get; set; }

        public bool Flag { get; set; }
    }

    public sealed class FitnessPrincipal : AuditableBaseEntity<int>
    {
        public bool IsActive { get; set; }
    }

    // Navigates to a different entity in its Criteria — the unsafe cross-source pattern.
    private sealed class NavigatingSpec : Specification<FitnessDependent, int>
    {
        public override Expression<Func<FitnessDependent, bool>> Criteria => d => d.Principal!.IsActive;
    }

    // Filters only on the entity's own scalar columns — safe on every engine.
    private sealed class ScalarOnlySpec : Specification<FitnessDependent, int>
    {
        public override Expression<Func<FitnessDependent, bool>> Criteria => d => d.PrincipalId == 1 && d.Flag;
    }

    // The same two shapes as query specifications: the rule walks the whole base chain, so
    // QuerySpecification -> Specification is still analyzed, includes and ordering notwithstanding.
    public sealed class NavigatingQuerySpec : QuerySpecification<FitnessDependent, int>
    {
        /// <summary>Initializes the navigating query specification.</summary>
        public NavigatingQuerySpec()
        {
            AddOrderBy(d => d.PrincipalId);
            ApplyPaging(skip: 0, take: 10);
        }

        /// <inheritdoc />
        public override Expression<Func<FitnessDependent, bool>> Criteria => d => d.Principal!.IsActive;
    }

    public sealed class ScalarOnlyQuerySpec : QuerySpecification<FitnessDependent, int>
    {
        /// <summary>Initializes the scalar-only query specification.</summary>
        public ScalarOnlyQuerySpec() => AddInclude(nameof(FitnessDependent.Principal));

        /// <inheritdoc />
        public override Expression<Func<FitnessDependent, bool>> Criteria => d => d.PrincipalId == 1 && d.Flag;
    }
}
