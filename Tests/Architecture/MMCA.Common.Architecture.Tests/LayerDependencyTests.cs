using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Clean Architecture layer-flow rules for the MMCA.Common framework packages, driven by the shared
/// rule library (<see cref="LayerDependencyTestsBase"/>) over <see cref="CommonArchitectureMap"/>.
/// </summary>
public sealed class LayerDependencyTests : LayerDependencyTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
