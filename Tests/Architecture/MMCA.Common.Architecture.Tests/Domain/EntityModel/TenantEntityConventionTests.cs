using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Domain.EntityModel;

/// <summary>
/// Tenancy marker rule (rubric section 4), framework-only: every concrete entity in MMCA.Common's
/// Domain and Infrastructure assemblies that declares or inherits a property named <c>TenantId</c>
/// must implement <see cref="ITenantEntity"/>. The marker is what applies the named <c>Tenant</c>
/// query filter and the <c>TenantSaveChangesInterceptor</c> stamp, so an entity that carries a tenant
/// column without the marker is readable and writable across tenants while looking scoped.
/// <para>
/// An entity here is a concrete class deriving from the framework entity hierarchy
/// (<c>MMCA.Common.Domain.Entities.BaseEntity&lt;TId&gt;</c>). The outbox, internal-command and audit
/// trail rows are deliberately outside that population: they are system rows that RECORD the tenant
/// of the scope that wrote them (a nullable ambient-context column restored on delivery), not rows a
/// tenant owns, and putting the read filter on them would hide work from the background drainers.
/// </para>
/// </summary>
public sealed class TenantEntityConventionTests
{
    private const string BaseEntityFullNamePrefix = "MMCA.Common.Domain.Entities.BaseEntity`";
    private const string TenantIdPropertyName = "TenantId";

    [Fact]
    public void FrameworkEntities_AreDiscovered_GateIsNotVacuous() =>
        FrameworkEntities().Should().NotBeEmpty(
            "the tenancy gate must scan at least one framework entity, or it passes while checking nothing");

    [Fact]
    public void EntitiesWithTenantId_ShouldImplement_ITenantEntity()
    {
        var offenders = FrameworkEntities()
            .Where(static t => t.GetProperty(TenantIdPropertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null
                && !typeof(ITenantEntity).IsAssignableFrom(t))
            .Select(static t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "an entity carrying a TenantId must implement ITenantEntity so the Tenant query filter and the "
            + "tenant save interceptor apply to it; offenders: " + string.Join(", ", offenders));
    }

    /// <summary>The concrete framework entities of the Domain and Infrastructure layers.</summary>
    private static List<Type> FrameworkEntities()
    {
        var map = new CommonArchitectureMap();

        return [.. map.OfLayer(Layer.Domain)
            .Concat(map.Infrastructure())
            .Distinct()
            .SelectMany(static a => a.GetTypes())
            .Where(static t => t is { IsClass: true, IsAbstract: false } && DerivesFromBaseEntity(t))];
    }

    private static bool DerivesFromBaseEntity(Type type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            var definition = baseType.IsGenericType ? baseType.GetGenericTypeDefinition() : baseType;
            if (definition.FullName?.StartsWith(BaseEntityFullNamePrefix, StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        return false;
    }
}
