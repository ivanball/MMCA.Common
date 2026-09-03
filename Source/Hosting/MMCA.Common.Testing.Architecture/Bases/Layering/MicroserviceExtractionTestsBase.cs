namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Transport-boundary fitness function for the modular-monolith → microservices extraction: MassTransit,
/// gRPC and Protobuf must never leak into Domain, Application or Shared, so a module behaves identically
/// in-process or extracted and the split stays reversible (ADR-006/007/008).
/// </summary>
public abstract class MicroserviceExtractionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void CoreLayers_ShouldNotDependOn_Transport() => ArchitectureRules.TransportDoesNotLeakIntoCoreLayers(Map);
}
