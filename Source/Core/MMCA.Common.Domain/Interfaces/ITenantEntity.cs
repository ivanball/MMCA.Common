namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// Opt-in marker: an entity carrying this interface belongs to exactly one tenant, and every read
/// and write the framework performs on it is scoped to the tenant resolved for the current request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads.</b> A named <c>Tenant</c> global query filter is applied to every non-owned entity
/// carrying this interface, alongside the existing <c>SoftDelete</c> filter; named filters compose
/// with AND, so a tenant never sees another tenant's rows and never sees soft-deleted ones. When no
/// tenant is resolved (a background service, a seeder, an admin flow) the filter is inert and the
/// query sees every tenant's rows: the system context is deliberately unrestricted.
/// </para>
/// <para>
/// <b>Writes.</b> <c>TenantSaveChangesInterceptor</c> stamps <see cref="TenantId"/> on insert and
/// refuses a save that would write across the tenant boundary. The property is therefore read-only
/// on the domain type: the value is not a caller's to choose, and EF writes it through the backing
/// field.
/// </para>
/// <para>
/// <b>The identifier is a <see langword="string"/>, capped at 64 characters.</b> It arrives from a
/// claim, a header, or configuration, all of which are strings, so a stronger domain type would
/// only add a conversion at every boundary without adding a guarantee.
/// </para>
/// <para>
/// <b>Marking is host-gated in practice.</b> A host that never resolves a tenant behaves exactly as
/// it did before: the filter short-circuits and the interceptor stamps nothing. Adopting tenancy is
/// the combination of marking entities, calling <c>AddMultiTenancy(configuration)</c>, and setting
/// <c>Tenancy:Enabled</c>.
/// </para>
/// </remarks>
public interface ITenantEntity
{
    /// <summary>
    /// Gets the identifier of the tenant that owns this entity. Assigned by the framework on
    /// insert; never set by application code.
    /// </summary>
    string TenantId { get; }
}
