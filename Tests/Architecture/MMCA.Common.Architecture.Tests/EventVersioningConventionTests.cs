using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Integration-event convention rules (SchemaVersion / BaseIntegrationEvent / namespace, ADR-010),
/// driven by the shared <see cref="EventConventionTestsBase"/>. Vacuous in the framework today (it ships
/// no concrete integration event) and fails the build the moment one is added that breaks the convention.
/// </summary>
public sealed class EventVersioningConventionTests : EventConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
