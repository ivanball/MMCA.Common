namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Minimal DDD aggregate fitness functions for repos that have no business modules (e.g. MMCA.Common):
/// the Domain layer exposes aggregate roots, and each is built via a static <c>Create(...)</c> factory
/// returning <c>Result&lt;T&gt;</c>. Module-bearing repos use the fuller <see cref="EntityConventionTestsBase"/>.
/// </summary>
public abstract class AggregateConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void Domain_ShouldExpose_AggregateRoots() => ArchitectureRules.DomainExposesAggregateRoots(Map);

    [Fact]
    public void AggregateRoots_ShouldHave_ResultReturningCreateFactory() => ArchitectureRules.AggregateRootsHaveResultFactory(Map);

    [Fact]
    public void AggregateRoots_ShouldHave_NoPublicConstructors() => ArchitectureRules.DomainAggregateRootsHaveNoPublicConstructors(Map);
}
