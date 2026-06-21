namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Base implementation of <see cref="IArchitectureMap"/>. A repo supplies <see cref="RepoToken"/> and
/// <see cref="DefineLayers"/> (using the <see cref="Framework"/> / <see cref="Module"/> factory
/// helpers); everything else — the projections, namespace derivation and module-isolation target
/// computation — is derived here so the per-repo map stays a flat declaration of assemblies.
/// Centralizing every namespace/assembly string in one file also fixes Ubuntu CI case-sensitivity
/// in one place.
/// </summary>
public abstract class ArchitectureMapBase : IArchitectureMap
{
    private readonly Lazy<IReadOnlyList<LayerRef>> _layers;

    protected ArchitectureMapBase() =>
        _layers = new Lazy<IReadOnlyList<LayerRef>>(() => [.. DefineLayers()]);

    /// <inheritdoc />
    public abstract string RepoToken { get; }

    /// <summary>Declare every layer assembly via the <see cref="Framework"/> / <see cref="Module"/> helpers.</summary>
    protected abstract IEnumerable<LayerRef> DefineLayers();

    /// <inheritdoc />
    public IReadOnlyList<LayerRef> Layers => _layers.Value;

    /// <inheritdoc />
    public IReadOnlyList<string> ModuleNames =>
        [.. Layers.Where(l => l.Module.Length > 0)
            .Select(l => l.Module)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <inheritdoc />
    public IEnumerable<Assembly> OfLayer(Layer layer) =>
        Layers.Where(l => l.Layer == layer).Select(l => l.Assembly).Distinct();

    /// <inheritdoc />
    public IEnumerable<Assembly> ModuleDomain() => ModuleLayer(Layer.Domain);

    /// <inheritdoc />
    public IEnumerable<Assembly> ModuleApplication() => ModuleLayer(Layer.Application);

    /// <inheritdoc />
    public IEnumerable<Assembly> ModuleShared() => ModuleLayer(Layer.Shared);

    /// <inheritdoc />
    public IEnumerable<Assembly> Infrastructure() => OfLayer(Layer.Infrastructure);

    /// <inheritdoc />
    public IEnumerable<Assembly> Api() => OfLayer(Layer.Api);

    /// <inheritdoc />
    public Assembly? For(string module, Layer layer) =>
        Layers.FirstOrDefault(l => l.Layer == layer
            && string.Equals(l.Module, module, StringComparison.Ordinal))?.Assembly;

    /// <inheritdoc />
    public string ModuleOf(Assembly assembly) =>
        Layers.FirstOrDefault(l => l.Assembly == assembly)?.Module ?? string.Empty;

    /// <inheritdoc />
    public string RootNamespace(string module, Layer layer) =>
        module.Length == 0
            ? $"MMCA.Common.{Segment(layer)}"
            : $"{RepoToken}.{module}.{Segment(layer)}";

    /// <inheritdoc />
    public string[] OtherModuleNamespaces(string module, Layer layer) =>
        [.. ModuleNames
            .Where(m => !string.Equals(m, module, StringComparison.Ordinal))
            .Select(m => RootNamespace(m, layer))];

    /// <summary>
    /// Walks up from the test assembly's base directory to the repo root (the directory containing
    /// the named solution file) so doc/config consistency tests can read committed files regardless
    /// of the runner's CWD.
    /// </summary>
    public static string FindRepoRoot(string solutionFileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, solutionFileName)))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate the repo root ({solutionFileName}) from {AppContext.BaseDirectory}.");
    }

    /// <summary>Declare a framework (MMCA.Common) layer assembly — owned by no business module.</summary>
    protected static LayerRef Framework(Layer layer, Assembly assembly) =>
        new(string.Empty, layer, assembly, $"MMCA.Common.{Segment(layer)}");

    /// <summary>Declare a per-module layer assembly.</summary>
    protected LayerRef Module(string module, Layer layer, Assembly assembly) =>
        new(module, layer, assembly, $"{RepoToken}.{module}.{Segment(layer)}");

    private IEnumerable<Assembly> ModuleLayer(Layer layer) =>
        Layers.Where(l => l.Layer == layer && l.Module.Length > 0).Select(l => l.Assembly);

    /// <summary>The namespace/assembly segment for a layer, e.g. <see cref="Layer.Api"/> → "API".</summary>
    internal static string Segment(Layer layer) => layer switch
    {
        Layer.Shared => "Shared",
        Layer.Domain => "Domain",
        Layer.Application => "Application",
        Layer.Infrastructure => "Infrastructure",
        Layer.Api => "API",
        Layer.Ui => "UI",
        Layer.Grpc => "Grpc",
        Layer.Contracts => "Contracts",
        Layer.ServiceHost => "Service",
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, message: null),
    };
}
