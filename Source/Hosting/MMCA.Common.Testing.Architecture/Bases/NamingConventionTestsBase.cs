namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Naming/sealing fitness functions across the CQRS + DDD building blocks: handlers, command/query
/// messages, validators, DTOs, domain events, invariants, EF configurations, specifications and
/// repositories each follow their established suffix (and sealing) convention.
/// </summary>
public abstract class NamingConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void Handlers_ShouldBeSealed_WithHandlerSuffix() => ArchitectureRules.HandlersAreSealedWithHandlerSuffix(Map);

    [Fact]
    public void Commands_ShouldHave_CommandOrRequestSuffix() => ArchitectureRules.CommandsHaveCommandOrRequestSuffix(Map);

    [Fact]
    public void Queries_ShouldHave_QuerySuffix() => ArchitectureRules.QueriesHaveQuerySuffix(Map);

    [Fact]
    public void Validators_ShouldHave_ValidatorOrRulesSuffix() => ArchitectureRules.ValidatorsHaveValidatorOrRulesSuffix(Map);

    [Fact]
    public void SharedDtos_ShouldHave_DtoOrLookupSuffix() => ArchitectureRules.SharedDtosHaveDtoOrLookupSuffix(Map);

    [Fact]
    public void DomainEvents_ShouldBeSealed_InDomainEventsNamespace() => ArchitectureRules.DomainEventsAreSealedInDomainEventsNamespace(Map);

    [Fact]
    public void InvariantClasses_ShouldBe_Static() => ArchitectureRules.InvariantClassesAreStatic(Map);

    [Fact]
    public void EfConfigurations_ShouldBeSealed_WithConfigurationSuffix() => ArchitectureRules.EfConfigurationsAreSealedWithConfigurationSuffix(Map);

    [Fact]
    public void Specifications_ShouldBeSealed_WithSpecificationSuffix() => ArchitectureRules.SpecificationsAreSealedWithSpecificationSuffix(Map);

    [Fact]
    public void Repositories_ShouldHave_RepositorySuffix() => ArchitectureRules.RepositoriesHaveRepositorySuffix(Map);
}
