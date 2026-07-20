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
    /// The layers every declared business module must register. Defaults to
    /// <see cref="RequiredLayers"/>; override for repos with deliberately thin modules
    /// (e.g. an API-plus-Application-only module) by trimming the list.
    /// </summary>
    protected virtual IReadOnlyList<Layer> RequiredModuleLayers => RequiredLayers;

    [Fact]
    public void LayerMap_DeclaresEveryExpectedLayer() => ArchitectureRules.LayerMapDeclaresLayers(Map, RequiredLayers);

    [Fact]
    public void LayerMap_ModulesDeclareEveryExpectedLayer() => ArchitectureRules.ModulesDeclareLayers(Map, RequiredModuleLayers);

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
