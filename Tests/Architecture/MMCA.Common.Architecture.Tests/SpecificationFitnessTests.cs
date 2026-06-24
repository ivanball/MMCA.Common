using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Testing.Architecture;
using Xunit;

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
}
