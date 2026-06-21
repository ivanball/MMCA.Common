namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Presentation fitness functions: controllers are thin and sealed, never reach Infrastructure or EF
/// Core directly, and inherit the framework <c>ApiControllerBase</c> for consistent Result → HTTP mapping.
/// </summary>
public abstract class ControllerConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>Controllers (by full name) that legitimately bypass <c>ApiControllerBase</c> (e.g. webhook endpoints).</summary>
    protected virtual IEnumerable<string> ControllersExemptFromApiControllerBase => [];

    [Fact]
    public void Controllers_ShouldNotDependOn_Infrastructure() => ArchitectureRules.ControllersDoNotDependOnInfrastructure(Map);

    [Fact]
    public void Controllers_ShouldNotDependOn_EntityFrameworkCore() => ArchitectureRules.ControllersDoNotDependOnEntityFrameworkCore(Map);

    [Fact]
    public void Controllers_ShouldBe_Sealed() => ArchitectureRules.ControllersAreSealed(Map);

    [Fact]
    public void Controllers_ShouldInherit_ApiControllerBase() => ArchitectureRules.ControllersInheritApiControllerBase(Map, ControllersExemptFromApiControllerBase);
}
