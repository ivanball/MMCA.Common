namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    // ── Module isolation: a module must not reach into another module's internal layers. ──
    // Cross-module communication is only allowed through the Shared (contract) layer.
    public static void ModuleDomainsAreIsolated(IArchitectureMap map) =>
        ModuleLayerIsolated(map, Layer.Domain, Layer.Domain,
            "modules must be independent — no cross-module Domain references");

    public static void ModuleApplicationsAreIsolated(IArchitectureMap map) =>
        ModuleLayerIsolated(map, Layer.Application, Layer.Application,
            "use Shared contracts for cross-module communication");

    public static void ModuleInfrastructuresAreIsolated(IArchitectureMap map) =>
        ModuleLayerIsolated(map, Layer.Infrastructure, Layer.Infrastructure,
            "no cross-module data access");

    public static void ModuleApisAreIsolated(IArchitectureMap map) =>
        ModuleLayerIsolated(map, Layer.Api, Layer.Api,
            "modules must not reference other module APIs");

    public static void ModuleDomainsDoNotReachOtherInfrastructures(IArchitectureMap map) =>
        ModuleLayerIsolated(map, Layer.Domain, Layer.Infrastructure,
            "Domain must never access another module's Infrastructure");

    public static void ModuleApplicationsDoNotReachOtherInfrastructures(IArchitectureMap map) =>
        ModuleLayerIsolated(map, Layer.Application, Layer.Infrastructure,
            "use Shared contracts, not another module's Infrastructure");

    /// <summary>
    /// The complete internal-layer cross product: every module's Domain, Application, Infrastructure,
    /// Api, Shared and UI against every OTHER module's Domain, Application, Infrastructure and Api.
    /// <para>
    /// The six named rules above cover the pairs worth their own failure message; this covers the
    /// rest, which nothing checked before. A per-module layer rule
    /// (<c>DomainDoesNotDependOnApplication</c> and friends) only forbids the SAME module's higher
    /// layers, and the compile-time layer guard only knows about <c>MMCA.Common.*</c> references, so
    /// a <c>Sales.Domain -> Catalog.Application</c> project reference passed every gate while
    /// compile-coupling two modules that ADR-007/008 promise can be extracted separately.
    /// </para>
    /// <para>
    /// Shared and UI are SOURCES only. The per-layer <c>SharedDoesNotDependOn*</c> and
    /// <c>UiDoesNotDependOn*</c> rules forbid only the same module's internal layers, so a module's
    /// Shared or UI reaching another module's Domain, Application, Infrastructure or Api was
    /// unchecked. As TARGETS they stay out of the product: a module's UI composing another module's
    /// UI, and any layer consuming another module's Shared contracts, are real, intended
    /// arrangements in the shipped apps (Shared to other Shared has its own rule,
    /// <see cref="ModuleSharedAreIsolated"/>).
    /// </para>
    /// </summary>
    public static void ModuleInternalLayersAreIsolated(IArchitectureMap map)
    {
        Layer[] sourceLayers = [Layer.Domain, Layer.Application, Layer.Infrastructure, Layer.Api, Layer.Shared, Layer.Ui];
        Layer[] internalLayers = [Layer.Domain, Layer.Application, Layer.Infrastructure, Layer.Api];
        foreach (var from in sourceLayers)
        {
            foreach (var to in internalLayers)
            {
                ModuleLayerIsolated(map, from, to,
                    "a module reaches another module only through its Shared contracts");
            }
        }
    }

    // ── Shared (contract) layer isolation: a module's Shared is contracts-only. ──
    public static void ModuleSharedDoesNotDependOnOwnInternalLayers(IArchitectureMap map)
    {
        Layer[] internalLayers = [Layer.Domain, Layer.Application, Layer.Infrastructure, Layer.Api];
        foreach (var sharedRef in map.Layers.Where(l => l.Layer == Layer.Shared && l.Module.Length > 0))
        {
            foreach (var internalLayer in internalLayers)
            {
                var forbidden = map.RootNamespace(sharedRef.Module, internalLayer);
                var result = Types.InAssembly(sharedRef.Assembly)
                    .ShouldNot()
                    .HaveDependencyOnAny(forbidden)
                    .GetResult();

                ArchitectureAssert.NoViolations(result,
                    $"{sharedRef.RootNamespace} → {forbidden}: Shared is a contracts-only layer");
            }
        }
    }

    public static void ModuleSharedAreIsolated(IArchitectureMap map) =>
        ModuleLayerIsolated(map, Layer.Shared, Layer.Shared,
            "each module's contracts are independent — no cross-module Shared references");

    public static void ModuleSharedIsFrameworkFree(IArchitectureMap map)
    {
        foreach (var sharedRef in map.Layers.Where(l => l.Layer == Layer.Shared && l.Module.Length > 0))
        {
            var result = Types.InAssembly(sharedRef.Assembly)
                .ShouldNot()
                .HaveDependencyOnAny("Microsoft.EntityFrameworkCore")
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{sharedRef.RootNamespace}: Shared is a contracts-only layer — no EF Core");
        }
    }

    private static void ModuleLayerIsolated(IArchitectureMap map, Layer from, Layer otherLayer, string reason)
    {
        foreach (var layerRef in map.Layers.Where(l => l.Layer == from && l.Module.Length > 0))
        {
            var otherNamespaces = map.OtherModuleNamespaces(layerRef.Module, otherLayer);
            if (otherNamespaces.Length == 0)
            {
                continue;
            }

            var result = Types.InAssembly(layerRef.Assembly)
                .ShouldNot()
                .HaveDependencyOnAny(otherNamespaces)
                .GetResult();

            ArchitectureAssert.NoViolations(result, $"{layerRef.RootNamespace}: {reason}");
        }
    }
}
