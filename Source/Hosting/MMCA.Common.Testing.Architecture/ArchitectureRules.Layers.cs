namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// The reusable architecture fitness functions, written once and parameterized by
/// <see cref="IArchitectureMap"/>. Each method asserts one rule across every applicable assembly the
/// map declares (framework and per-module), so a repo's test classes reduce to a sealed subclass of
/// the matching base supplying its own map.
/// </summary>
public static partial class ArchitectureRules
{
    // ── Clean Architecture layer flow: each layer may only reference layers below it ──
    public static void DomainDoesNotDependOnApplication(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Domain, Layer.Application,
            "Domain is the innermost layer — it must not depend on Application");

    public static void DomainDoesNotDependOnInfrastructure(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Domain, Layer.Infrastructure,
            "Domain must not depend on Infrastructure");

    public static void DomainDoesNotDependOnApi(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Domain, Layer.Api,
            "Domain must not depend on API/Presentation");

    public static void ApplicationDoesNotDependOnInfrastructure(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Application, Layer.Infrastructure,
            "Application must not depend on Infrastructure — use repository/UoW abstractions");

    public static void ApplicationDoesNotDependOnApi(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Application, Layer.Api,
            "Application must not depend on API/Presentation");

    public static void InfrastructureDoesNotDependOnApi(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Infrastructure, Layer.Api,
            "Infrastructure implements Application interfaces, not Presentation");

    public static void SharedDoesNotDependOnDomain(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Shared, Layer.Domain,
            "Shared is the foundation layer — it must not depend on Domain");

    public static void SharedDoesNotDependOnApplication(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Shared, Layer.Application,
            "Shared must not depend on Application");

    public static void SharedDoesNotDependOnInfrastructure(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Shared, Layer.Infrastructure,
            "Shared must not depend on Infrastructure");

    public static void SharedDoesNotDependOnApi(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Shared, Layer.Api,
            "Shared must not depend on API");

    public static void UiDoesNotDependOnDomain(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Ui, Layer.Domain,
            "UI depends only on Shared for Blazor WASM compatibility — it must not depend on Domain");

    public static void UiDoesNotDependOnApplication(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Ui, Layer.Application,
            "UI depends only on Shared for Blazor WASM compatibility — it must not depend on Application");

    public static void UiDoesNotDependOnInfrastructure(IArchitectureMap map) =>
        LayerNotDependOnLayer(map, Layer.Ui, Layer.Infrastructure,
            "UI must not depend on Infrastructure");

    /// <summary>
    /// The architecture map declares at least one assembly for every layer in
    /// <paramref name="required"/>. The per-layer dependency rules iterate
    /// <c>map.Layers.Where(l => l.Layer == from)</c>, so a map that silently omits a layer gets
    /// vacuously green layer tests; this completeness check turns that omission into a failure.
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="required">The layers the map must declare (framework or per-module).</param>
    public static void LayerMapDeclaresLayers(IArchitectureMap map, IEnumerable<Layer> required)
    {
        var missing = required
            .Where(layer => !map.Layers.Any(l => l.Layer == layer))
            .Select(layer => $"  - no assembly is registered under Layer.{layer}");

        ArchitectureAssert.NoViolations(missing,
            "the architecture map must declare every expected layer — a missing layer makes its dependency rules vacuously green");
    }

    /// <summary>
    /// Every module the map declares registers an assembly for each layer in
    /// <paramref name="required"/>, so no per-module layer rule can pass vacuously because a
    /// module's assembly was forgotten in the map.
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="required">The layers every declared module must register.</param>
    public static void ModulesDeclareLayers(IArchitectureMap map, IEnumerable<Layer> required)
    {
        var requiredList = required.ToList();
        var missing = map.ModuleNames
            .SelectMany(module => requiredList
                .Where(layer => map.For(module, layer) is null)
                .Select(layer => $"  - module '{module}' declares no Layer.{layer} assembly"));

        ArchitectureAssert.NoViolations(missing,
            "every declared module must register its expected layer assemblies so the per-module layer rules run non-vacuously");
    }

    private static void LayerNotDependOnLayer(IArchitectureMap map, Layer from, Layer to, string reason)
    {
        foreach (var layerRef in map.Layers.Where(l => l.Layer == from))
        {
            var forbidden = map.RootNamespace(layerRef.Module, to);
            var result = Types.InAssembly(layerRef.Assembly)
                .ShouldNot()
                .HaveDependencyOnAny(forbidden)
                .GetResult();

            ArchitectureAssert.NoViolations(result, $"{layerRef.RootNamespace} → {forbidden}: {reason}");
        }
    }
}
