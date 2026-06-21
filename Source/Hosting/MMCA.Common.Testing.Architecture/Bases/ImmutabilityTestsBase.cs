namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Immutability fitness functions: DTOs, command/query messages, domain events, integration events and
/// value objects expose no public mutable (non-init) setters, so a contract or message cannot be
/// mutated after construction. Value objects are additionally sealed and confined to the Shared layer.
/// </summary>
public abstract class ImmutabilityTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void Dtos_ShouldBe_Immutable() => ArchitectureRules.DtosAreImmutable(Map);

    [Fact]
    public void CommandsAndQueries_ShouldBe_Immutable() => ArchitectureRules.CommandsAndQueriesAreImmutable(Map);

    [Fact]
    public void DomainEvents_ShouldBe_Immutable() => ArchitectureRules.DomainEventsAreImmutable(Map);

    [Fact]
    public void IntegrationEvents_ShouldBe_Immutable() => ArchitectureRules.IntegrationEventsAreImmutable(Map);

    [Fact]
    public void ValueObjects_ShouldBe_ImmutableSealedAndInShared() => ArchitectureRules.ValueObjectsAreImmutableSealedInShared(Map);
}
