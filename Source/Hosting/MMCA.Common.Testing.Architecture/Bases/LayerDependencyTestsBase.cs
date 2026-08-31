namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Clean Architecture layer-flow fitness functions. A repo derives a sealed subclass supplying its
/// <see cref="Map"/>; the rules run across every framework and per-module assembly the map declares.
/// </summary>
public abstract class LayerDependencyTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// The layers the map must declare at least once (framework or per-module) for the dependency
    /// rules to run non-vacuously. Defaults to the five core Clean Architecture layers every MMCA
    /// repo registers; override to trim for a repo that legitimately lacks one of them.
    /// </summary>
    protected virtual IReadOnlyList<Layer> RequiredLayers =>
        [Layer.Shared, Layer.Domain, Layer.Application, Layer.Infrastructure, Layer.Api];

    /// <summary>
    /// The layers every declared business module must register, unless the module names itself in
    /// <see cref="ModuleRequiredLayerOverrides"/>. Defaults to <see cref="RequiredLayers"/>.
    /// <para>
    /// Prefer the per-module override to trimming this list: trimming applies to EVERY module, so one
    /// thin module would stop the rule from catching a forgotten assembly anywhere in the repo.
    /// </para>
    /// </summary>
    protected virtual IReadOnlyList<Layer> RequiredModuleLayers => RequiredLayers;

    /// <summary>
    /// Per-module replacements for <see cref="RequiredModuleLayers"/>, keyed by module name. Empty by
    /// default: every module is held to the same list.
    /// <para>
    /// Override this for a deliberately thin module, one that owns no aggregate and no persistence
    /// and therefore legitimately ships no Domain or Infrastructure assembly (a SignalR hub module,
    /// say, that is Shared plus Application plus Api and nothing else). Listing it here keeps full
    /// enforcement on every other module, so the exception is recorded where it is true rather than
    /// paid for by the whole repo.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// protected override IReadOnlyDictionary&lt;string, IReadOnlyList&lt;Layer&gt;&gt; ModuleRequiredLayerOverrides
    ///     =&gt; new Dictionary&lt;string, IReadOnlyList&lt;Layer&gt;&gt;(StringComparer.Ordinal)
    ///     {
    ///         ["Notification"] = [Layer.Shared, Layer.Application, Layer.Api],
    ///     };
    /// </code>
    /// </example>
    protected virtual IReadOnlyDictionary<string, IReadOnlyList<Layer>> ModuleRequiredLayerOverrides
        => new Dictionary<string, IReadOnlyList<Layer>>(StringComparer.Ordinal);

    [Fact]
    public void LayerMap_DeclaresEveryExpectedLayer() => ArchitectureRules.LayerMapDeclaresLayers(Map, RequiredLayers);

    [Fact]
    public void LayerMap_ModulesDeclareEveryExpectedLayer() =>
        ArchitectureRules.ModulesDeclareLayers(Map, RequiredModuleLayers, ModuleRequiredLayerOverrides);

    [Fact]
    public void Domain_ShouldNotDependOn_Application() => ArchitectureRules.DomainDoesNotDependOnApplication(Map);

    [Fact]
    public void Domain_ShouldNotDependOn_Infrastructure() => ArchitectureRules.DomainDoesNotDependOnInfrastructure(Map);

    [Fact]
    public void Domain_ShouldNotDependOn_Api() => ArchitectureRules.DomainDoesNotDependOnApi(Map);

    [Fact]
    public void Application_ShouldNotDependOn_Infrastructure() => ArchitectureRules.ApplicationDoesNotDependOnInfrastructure(Map);

    [Fact]
    public void Application_ShouldNotDependOn_Api() => ArchitectureRules.ApplicationDoesNotDependOnApi(Map);

    [Fact]
    public void Infrastructure_ShouldNotDependOn_Api() => ArchitectureRules.InfrastructureDoesNotDependOnApi(Map);

    [Fact]
    public void Shared_ShouldNotDependOn_Domain() => ArchitectureRules.SharedDoesNotDependOnDomain(Map);

    [Fact]
    public void Shared_ShouldNotDependOn_Application() => ArchitectureRules.SharedDoesNotDependOnApplication(Map);

    [Fact]
    public void Shared_ShouldNotDependOn_Infrastructure() => ArchitectureRules.SharedDoesNotDependOnInfrastructure(Map);

    [Fact]
    public void Shared_ShouldNotDependOn_Api() => ArchitectureRules.SharedDoesNotDependOnApi(Map);

    [Fact]
    public void Ui_ShouldNotDependOn_Domain() => ArchitectureRules.UiDoesNotDependOnDomain(Map);

    [Fact]
    public void Ui_ShouldNotDependOn_Application() => ArchitectureRules.UiDoesNotDependOnApplication(Map);

    [Fact]
    public void Ui_ShouldNotDependOn_Infrastructure() => ArchitectureRules.UiDoesNotDependOnInfrastructure(Map);
}
