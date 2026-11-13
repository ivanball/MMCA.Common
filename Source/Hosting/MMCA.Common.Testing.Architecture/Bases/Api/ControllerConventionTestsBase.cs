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

    /// <summary>
    /// No module controller overrides a base read action only to add <c>[AllowAnonymous]</c> or
    /// <c>[OutputCache]</c>: <c>[PublicCachedReads]</c> on the class marks the inherited reads.
    /// </summary>
    [Fact]
    public void ReadOverrides_ShouldNotRedeclare_PublicCaching() =>
        ArchitectureRules.ReadOverridesDoNotRedeclarePublicCaching(ArchitectureRules.ModuleApiConcreteClasses(Map));

    /// <summary>
    /// No module action carrying <c>[SupportsIfMatch]</c> declares 412 or 428 by hand: the attribute
    /// documents both.
    /// </summary>
    [Fact]
    public void ConditionalActions_ShouldNotRedeclare_PreconditionResponses() =>
        ArchitectureRules.ConditionalActionsDoNotRedeclarePreconditionResponses(ArchitectureRules.ModuleApiConcreteClasses(Map));
}
