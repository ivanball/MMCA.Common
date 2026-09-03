namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Integration-event convention fitness functions (ADR-010): every concrete integration event inherits
/// <c>BaseIntegrationEvent</c>, declares an <c>int SchemaVersion</c>, and lives in a
/// <c>*.IntegrationEvents</c> namespace in the Shared layer. The last two facts police the upcasters
/// that carry a retired contract forward (ADR-090); a repo with no upcasters passes them vacuously.
/// </summary>
public abstract class EventConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void IntegrationEvents_ShouldDeclare_SchemaVersion() => ArchitectureRules.IntegrationEventsDeclareSchemaVersion(Map);

    [Fact]
    public void IntegrationEvents_ShouldInherit_BaseIntegrationEvent() => ArchitectureRules.IntegrationEventsInheritBaseIntegrationEvent(Map);

    [Fact]
    public void IntegrationEvents_ShouldResideIn_SharedIntegrationEventsNamespace() => ArchitectureRules.IntegrationEventsResideInSharedIntegrationEventsNamespace(Map);

    [Fact]
    public void EventUpcasters_ShouldHave_UniqueSourceTypes() => ArchitectureRules.EventUpcastersHaveUniqueSourceTypes(Map);

    [Fact]
    public void EventUpcasters_ShouldIncrease_SchemaVersion() => ArchitectureRules.EventUpcastersIncreaseSchemaVersion(Map);
}
