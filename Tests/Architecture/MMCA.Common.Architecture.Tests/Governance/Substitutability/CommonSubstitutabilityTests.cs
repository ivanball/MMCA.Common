using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance.Substitutability;

/// <summary>
/// Runs the shared substitutability rule (<see cref="SubstitutabilityTestsBase"/>) against
/// MMCA.Common's own assemblies, the same way MMCA.ADC and MMCA.Store subclass it for theirs. No
/// member is excluded: every framework override and interface implementation honours its contract.
/// </summary>
public sealed class CommonSubstitutabilityTests : SubstitutabilityTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
