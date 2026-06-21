namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Modular-monolith boundary fitness functions: a module must not reach into another module's internal
/// layers; cross-module communication is only allowed through the Shared (contract) layer. Vacuous for
/// single-module / module-less repos (e.g. MMCA.Common).
/// </summary>
public abstract class ModuleIsolationTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void ModuleDomains_ShouldBe_Isolated() => ArchitectureRules.ModuleDomainsAreIsolated(Map);

    [Fact]
    public void ModuleApplications_ShouldBe_Isolated() => ArchitectureRules.ModuleApplicationsAreIsolated(Map);

    [Fact]
    public void ModuleInfrastructures_ShouldBe_Isolated() => ArchitectureRules.ModuleInfrastructuresAreIsolated(Map);

    [Fact]
    public void ModuleApis_ShouldBe_Isolated() => ArchitectureRules.ModuleApisAreIsolated(Map);

    [Fact]
    public void ModuleDomains_ShouldNotReach_OtherModuleInfrastructures() => ArchitectureRules.ModuleDomainsDoNotReachOtherInfrastructures(Map);

    [Fact]
    public void ModuleApplications_ShouldNotReach_OtherModuleInfrastructures() => ArchitectureRules.ModuleApplicationsDoNotReachOtherInfrastructures(Map);
}
