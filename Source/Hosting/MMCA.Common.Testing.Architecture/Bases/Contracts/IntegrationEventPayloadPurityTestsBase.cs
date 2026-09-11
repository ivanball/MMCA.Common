namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Payload-purity fitness functions for the cross-service async API (ADR-010).
/// <para>
/// <b>Integration events are the public contract; domain events are not the public API.</b> A domain
/// event is an internal fact inside one bounded context, free to change with the model that raises
/// it. An integration event is the opposite: another service deserializes it by shape, is deployed
/// on its own schedule, and cannot be asked to redeploy because a producer renamed a field. The two
/// rules here keep that distinction structural rather than cultural.
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// The event type ships from a <c>*.Shared</c> assembly: a consuming module may reference only
/// Shared, so an event declared anywhere else is a contract nobody is allowed to reference.
/// </description>
/// </item>
/// <item>
/// <description>
/// No property on the wire shape reaches a type declared in a <c>*.Domain</c> assembly, including
/// through a nested payload record this repo declares. Publishing an entity or a domain value
/// object exports the producer's internal model, so an internal refactor becomes a breaking change
/// for every consumer, and fields the producer never meant to share ride along on the wire.
/// </description>
/// </item>
/// </list>
/// <para>
/// Both rules are vacuous for a module-less map (the framework itself): MMCA.Common is not a module,
/// and its own events are governed by its public API baseline instead.
/// </para>
/// </summary>
public abstract class IntegrationEventPayloadPurityTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void IntegrationEvents_ShouldShipFrom_SharedAssemblies() => ArchitectureRules.IntegrationEventsLiveInSharedAssemblies(Map);

    [Fact]
    public void IntegrationEventPayloads_ShouldNotExpose_DomainTypes() => ArchitectureRules.IntegrationEventPayloadsAreDomainFree(Map);
}
