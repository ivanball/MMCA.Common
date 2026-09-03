using System.Globalization;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Infrastructure.Context;

/// <summary>
/// Scoped service holding the tenant the current request runs as. Unresolved until
/// <see cref="SetTenant"/> is called, which is the state every background service, seeder and
/// design-time tool stays in.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    /// <inheritdoc />
    public string? TenantId { get; private set; }

    /// <inheritdoc />
    public bool IsResolved => TenantId is not null;

    /// <inheritdoc />
    public void SetTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (TenantId is null)
        {
            TenantId = tenantId;
            return;
        }

        // Idempotent for the same value: the middleware and a background worker that re-asserts the
        // tenant on the same scope must not fight each other.
        if (string.Equals(TenantId, tenantId, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(string.Format(
            CultureInfo.InvariantCulture,
            "The tenant for this scope is already \"{0}\" and cannot be changed to \"{1}\". "
            + "Anything already read or tracked in this scope was scoped to the first tenant; "
            + "start a new scope for a different tenant.",
            TenantId,
            tenantId));
    }
}
