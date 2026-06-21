namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// The architectural layers a fitness function can reason about. Not every repo has every layer:
/// <see cref="Layer.Ui"/>, <see cref="Layer.Grpc"/>, <see cref="Layer.Contracts"/> and
/// <see cref="Layer.ServiceHost"/> are optional and simply omitted from a repo's map when absent,
/// so a rule that iterates them is vacuously satisfied with no compile dependency on the assembly.
/// </summary>
public enum Layer
{
    Shared,
    Domain,
    Application,
    Infrastructure,
    Api,
    Ui,
    Grpc,
    Contracts,
    ServiceHost,
}

/// <summary>
/// One assembly in a repo's architecture, tagged with its owning module and layer. The
/// <see cref="Module"/> is the empty string for framework (MMCA.Common) layers that belong to no
/// business module.
/// </summary>
/// <param name="Module">The business module name (e.g. "Catalog"), or "" for framework layers.</param>
/// <param name="Layer">The architectural layer this assembly implements.</param>
/// <param name="Assembly">The compiled assembly.</param>
/// <param name="RootNamespace">The assembly's root namespace (e.g. "MMCA.Store.Catalog.Application").</param>
public sealed record LayerRef(string Module, Layer Layer, Assembly Assembly, string RootNamespace);

/// <summary>
/// The single per-repo seam every architecture fitness function keys off. Each repo supplies one
/// implementation (e.g. <c>StoreArchitectureMap</c>) declaring its layer/module assemblies; the
/// shared rule library and abstract test bases consume <em>only</em> this interface, so a rule is
/// written once and runs identically across MMCA.Common, MMCA.Store and MMCA.ADC.
/// </summary>
public interface IArchitectureMap
{
    /// <summary>The repo's assembly/namespace prefix, e.g. "MMCA.Common" | "MMCA.Store" | "MMCA.ADC".</summary>
    string RepoToken { get; }

    /// <summary>The business module names (e.g. ["Catalog","Sales","Identity"]); empty for MMCA.Common.</summary>
    IReadOnlyList<string> ModuleNames { get; }

    /// <summary>Every registered layer assembly, framework and per-module.</summary>
    IReadOnlyList<LayerRef> Layers { get; }

    /// <summary>All layer assemblies of a given kind (framework + module), across every module.</summary>
    IEnumerable<Assembly> OfLayer(Layer layer);

    /// <summary>The per-module <see cref="Layer.Domain"/> assemblies (excludes the framework layer).</summary>
    IEnumerable<Assembly> ModuleDomain();

    /// <summary>The per-module <see cref="Layer.Application"/> assemblies (excludes the framework layer).</summary>
    IEnumerable<Assembly> ModuleApplication();

    /// <summary>The per-module <see cref="Layer.Shared"/> assemblies (excludes the framework layer).</summary>
    IEnumerable<Assembly> ModuleShared();

    /// <summary>Every <see cref="Layer.Infrastructure"/> assembly (framework + per-module).</summary>
    IEnumerable<Assembly> Infrastructure();

    /// <summary>Every <see cref="Layer.Api"/> assembly (framework + per-module).</summary>
    IEnumerable<Assembly> Api();

    /// <summary>The assembly for a specific (module, layer), or null when that layer is absent.</summary>
    Assembly? For(string module, Layer layer);

    /// <summary>The module that owns an assembly, or "" for a framework assembly / unknown.</summary>
    string ModuleOf(Assembly assembly);

    /// <summary>The root namespace for a (module, layer), e.g. "MMCA.Store.Catalog.Application".</summary>
    string RootNamespace(string module, Layer layer);

    /// <summary>
    /// The root namespaces of the SAME layer in every OTHER module — the forbidden targets for a
    /// module-isolation rule. Empty for framework layers and single-module repos.
    /// </summary>
    string[] OtherModuleNamespaces(string module, Layer layer);
}
