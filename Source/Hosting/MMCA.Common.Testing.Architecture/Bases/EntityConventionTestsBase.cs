namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// DDD entity/aggregate fitness functions: entities are sealed and live only in Domain, aggregate roots
/// are built through a static <c>Create(...)</c> factory returning <c>Result&lt;T&gt;</c> with no public
/// constructor, and DTOs/requests stay out of Domain and Infrastructure.
/// </summary>
public abstract class EntityConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void Domain_ShouldExpose_AggregateRoots() => ArchitectureRules.DomainExposesAggregateRoots(Map);

    [Fact]
    public void AggregateRoots_ShouldHave_ResultReturningCreateFactory() => ArchitectureRules.AggregateRootsHaveResultFactory(Map);

    [Fact]
    public void AggregateRoots_ShouldHave_NoPublicConstructors() => ArchitectureRules.AggregateRootsHaveNoPublicConstructors(Map);

    [Fact]
    public void DomainEntities_ShouldBe_Sealed() => ArchitectureRules.DomainEntitiesAreSealed(Map);

    [Fact]
    public void DomainEntities_ShouldReside_InDomainLayer() => ArchitectureRules.EntitiesResideInDomainLayer(Map);

    [Fact]
    public void DtosAndRequests_ShouldNotResideIn_DomainOrInfrastructure() => ArchitectureRules.DtosAndRequestsAreNotInDomainOrInfrastructure(Map);
}
