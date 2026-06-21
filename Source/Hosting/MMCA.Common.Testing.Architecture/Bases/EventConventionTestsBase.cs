namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Integration-event convention fitness functions (ADR-010): every concrete integration event inherits
/// <c>BaseIntegrationEvent</c>, declares an <c>int SchemaVersion</c>, and lives in a
/// <c>*.IntegrationEvents</c> namespace in the Shared layer.
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
}
