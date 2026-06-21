namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Clean Architecture layer-flow fitness functions. A repo derives a sealed subclass supplying its
/// <see cref="Map"/>; the rules run across every framework and per-module assembly the map declares.
/// </summary>
public abstract class LayerDependencyTestsBase
{
    protected abstract IArchitectureMap Map { get; }

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
