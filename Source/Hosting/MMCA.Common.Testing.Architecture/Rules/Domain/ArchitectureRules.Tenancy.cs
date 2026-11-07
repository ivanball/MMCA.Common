namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string TenantEntityInterfaceFullName = "MMCA.Common.Domain.Interfaces.ITenantEntity";
    private const string TenantIdPropertyName = "TenantId";

    /// <summary>
    /// Every concrete entity in the Domain and Infrastructure layers (framework and module) that
    /// declares or inherits a property named <c>TenantId</c> implements <c>ITenantEntity</c>. The
    /// marker is what applies the named <c>Tenant</c> query filter and the tenant save interceptor, so
    /// an entity carrying a tenant column without it is readable and writable across tenants while
    /// looking scoped (ADR-073).
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    public static void EntitiesWithTenantIdImplementTenantMarker(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var violations = TenancyEntities(map.OfLayer(Layer.Domain).Concat(map.Infrastructure()))
            .Where(t => t.GetProperty(TenantIdPropertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null
                && !ImplementsTenantMarker(t))
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(violations,
            "an entity carrying a TenantId must implement ITenantEntity so the Tenant query filter and the "
            + "tenant save interceptor apply to it");
    }

    /// <summary>
    /// Every concrete module entity is either tenant-scoped (implements <c>ITenantEntity</c>) or named
    /// in <paramref name="globalEntityTypes"/>. Closes the gap ADR-073 records: a forgotten marker on a
    /// new entity otherwise fails no build and no test, and in shared-schema tenancy it silently puts
    /// that entity's rows in every tenant's queries. Declaring an entity global is a reviewed decision
    /// in the test subclass, not an absence.
    /// <para>
    /// Framework entities are out of scope: whether a framework row is tenant-owned is the framework's
    /// decision, and <see cref="EntitiesWithTenantIdImplementTenantMarker"/> already covers them.
    /// </para>
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="globalEntityTypes">
    /// Type full names or namespace prefixes of module entities that are deliberately shared by all
    /// tenants (reference data, for example). Matched ordinally, exactly as the soft-delete allowlist.
    /// </param>
    public static void ModuleEntitiesAreTenantScopedOrDeclaredGlobal(
        IArchitectureMap map,
        IReadOnlyCollection<string> globalEntityTypes)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(globalEntityTypes);

        var entities = TenancyEntities(map.ModuleDomain()).ToList();
        entities.Should().NotBeEmpty(
            because: "the tenant-scope rule must find module entities, or it passes while checking nothing");

        var violations = entities
            .Where(t => !ImplementsTenantMarker(t) && !IsAllowed(t.FullName ?? t.Name, globalEntityTypes))
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(violations,
            "every module entity must implement ITenantEntity or be declared global in the test subclass "
            + "(GlobalEntityTypes); a forgotten marker shares the entity's rows across tenants");
    }

    private static IEnumerable<Type> TenancyEntities(IEnumerable<Assembly> assemblies) =>
        assemblies.Distinct()
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => t.InheritsAuditableEntity);

    private static bool ImplementsTenantMarker(Type type) =>
        type.GetInterfaces().Any(i => string.Equals(i.FullName, TenantEntityInterfaceFullName, StringComparison.Ordinal));
}
