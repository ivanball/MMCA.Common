namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Shared (contract) layer fitness functions: a module's Shared is contracts-only (it must not depend
/// on its own internal layers, on another module's Shared, or on EF Core), and its enums are
/// wire-stable, with an explicit value on every member.
/// </summary>
public abstract class SharedLayerTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// The source directories of the module Shared projects, for the source-scanning enum rule.
    /// Defaults to every folder under the repo's <c>Source/</c> tree named after a business module's
    /// Shared root namespace (the project folder convention, e.g.
    /// <c>Source/Modules/Sales/MMCA.Store.Sales.Shared</c>), with the repo root found from
    /// <c>{RepoToken}.slnx</c>. Framework Shared layers are not scanned. Override for a custom layout.
    /// </summary>
    /// <returns>The directories whose <c>.cs</c> files the enum rule reads.</returns>
    protected virtual IEnumerable<string> ModuleSharedSourceDirectories()
    {
        var sourceRoot = Path.Combine(ArchitectureMapBase.FindRepoRoot($"{Map.RepoToken}.slnx"), "Source");

        foreach (var projectName in ModuleSharedProjectNames())
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceRoot, projectName, SearchOption.AllDirectories))
            {
                yield return directory;
            }
        }
    }

    [Fact]
    public void ModuleShared_ShouldNotDependOn_OwnInternalLayers() => ArchitectureRules.ModuleSharedDoesNotDependOnOwnInternalLayers(Map);

    [Fact]
    public void ModuleShared_ShouldBe_Isolated() => ArchitectureRules.ModuleSharedAreIsolated(Map);

    [Fact]
    public void ModuleShared_ShouldNotDependOn_EntityFrameworkCore() => ArchitectureRules.ModuleSharedIsFrameworkFree(Map);

    [Fact]
    public void ModuleShared_Enums_ShouldHave_ExplicitValues()
    {
        var directories = ModuleSharedSourceDirectories().ToList();

        if (ModuleSharedProjectNames().Any())
        {
            directories.Should().NotBeEmpty(
                because: "the map declares module Shared layers but no project folder of that name exists under Source/: "
                    + "the folders moved, and a vacuous scan would verify nothing. Override ModuleSharedSourceDirectories()");
        }

        ArchitectureRules.ModuleSharedEnumValuesAreExplicit(directories);
    }

    /// <summary>The distinct root namespaces (project folder names) of the module Shared layers.</summary>
    private IEnumerable<string> ModuleSharedProjectNames() =>
        Map.Layers
            .Where(layer => layer.Layer == Layer.Shared && !string.IsNullOrEmpty(layer.Module))
            .Select(layer => layer.RootNamespace)
            .Distinct(StringComparer.Ordinal);
}
