namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Provides access to the tenant the current scope runs as. Set by
/// <c>TenantResolutionMiddleware</c> from a claim or a header, and read by the persistence layer
/// (query filters, the tenant save interceptor, per-tenant database routing) and by the caching
/// decorators.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="ICorrelationContext"/>: one scoped instance per request, populated once at
/// the edge. Unlike the correlation id there is no generated fallback. An unresolved tenant is a
/// meaningful state (a background service, a seeder, an admin flow) and reads as "see everything",
/// so inventing a value would silently scope a system operation to a tenant that does not exist.
/// </para>
/// <para>
/// <b>One scope, one tenant.</b> <see cref="SetTenant"/> accepts the value it already holds and
/// throws on a different one. A scope whose tenant changed mid-flight would have already read rows
/// under the previous tenant, and there is no honest way to reconcile that after the fact.
/// </para>
/// </remarks>
public interface ITenantContext
{
    /// <summary>
    /// Gets the tenant identifier for the current scope, or <see langword="null"/> when no tenant
    /// has been resolved.
    /// </summary>
    string? TenantId { get; }

    /// <summary>Gets a value indicating whether a tenant has been resolved for this scope.</summary>
    bool IsResolved { get; }

    /// <summary>
    /// Sets the tenant for the current scope. Idempotent for the value already held.
    /// </summary>
    /// <param name="tenantId">The tenant identifier to set.</param>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// A different tenant was already resolved for this scope.
    /// </exception>
    void SetTenant(string tenantId);
}
