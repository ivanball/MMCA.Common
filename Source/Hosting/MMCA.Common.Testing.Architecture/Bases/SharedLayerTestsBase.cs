namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Shared (contract) layer fitness functions: a module's Shared is contracts-only — it must not depend
/// on its own internal layers, on another module's Shared, or on EF Core.
/// </summary>
public abstract class SharedLayerTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void ModuleShared_ShouldNotDependOn_OwnInternalLayers() => ArchitectureRules.ModuleSharedDoesNotDependOnOwnInternalLayers(Map);

    [Fact]
    public void ModuleShared_ShouldBe_Isolated() => ArchitectureRules.ModuleSharedAreIsolated(Map);

    [Fact]
    public void ModuleShared_ShouldNotDependOn_EntityFrameworkCore() => ArchitectureRules.ModuleSharedIsFrameworkFree(Map);
}
