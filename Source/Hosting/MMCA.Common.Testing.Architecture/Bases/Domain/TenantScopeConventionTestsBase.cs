namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Tenancy fitness functions for a host that enables multi-tenancy (ADR-073): every entity carrying a
/// <c>TenantId</c> implements <c>ITenantEntity</c>, and every module entity is either tenant-scoped or
/// declared global in <see cref="GlobalEntityTypes"/>.
/// <para>
/// Subclass this only in a tenancy host. Adoption: subclass, run once, and for each reported entity
/// either add the <c>ITenantEntity</c> marker or add it to <see cref="GlobalEntityTypes"/> with a
/// comment saying why every tenant shares its rows. The list is the point of the rule: it turns
/// "tenancy is opt-in per entity" into a reviewed inventory of every entity that is not scoped.
/// </para>
/// </summary>
public abstract class TenantScopeConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Type full names or namespace prefixes of module entities shared by every tenant. Empty by
    /// default, which requires every module entity to be tenant-scoped.
    /// </summary>
    protected virtual IReadOnlyCollection<string> GlobalEntityTypes => [];

    [Fact]
    public void EntitiesWithTenantId_ShouldImplement_ITenantEntity() =>
        ArchitectureRules.EntitiesWithTenantIdImplementTenantMarker(Map);

    [Fact]
    public void ModuleEntities_ShouldBeTenantScoped_OrDeclaredGlobal() =>
        ArchitectureRules.ModuleEntitiesAreTenantScopedOrDeclaredGlobal(Map, GlobalEntityTypes);
}
