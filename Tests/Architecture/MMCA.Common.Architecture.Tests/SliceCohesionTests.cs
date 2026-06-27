using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Vertical-slice cohesion rules (§5), driven by the shared <see cref="SliceCohesionTestsBase"/>:
/// the framework's Notifications use-case slices keep each command/query, its handler, and its
/// validator in one namespace. Fails the build the moment a handler is stranded away from its contract.
/// </summary>
public sealed class SliceCohesionTests : SliceCohesionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
