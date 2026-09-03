using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Contracts;

/// <summary>
/// Integration-event convention rules (SchemaVersion / BaseIntegrationEvent / namespace, ADR-010),
/// driven by the shared <see cref="EventConventionTestsBase"/>. The framework ships one concrete
/// integration event today (<c>OutputCacheEvictionRequested</c>, in
/// <c>MMCA.Common.Domain.IntegrationEvents</c>); the rules fail the build the moment one is added
/// that breaks the convention.
/// </summary>
public sealed class EventVersioningConventionTests : EventConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
