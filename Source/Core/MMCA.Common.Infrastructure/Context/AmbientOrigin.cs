using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Infrastructure.Context;

/// <summary>
/// The one place the framework snapshots and restores the ambient request context around a hop it
/// cannot stay on the same call stack for: a deferred command, an outbox row, a broker message.
/// <para>
/// Every such hop captures the same four values (user id, roles, tenant, correlation id) and
/// restores them the same way, so the capture and the restore live here rather than once per
/// mechanism. Before this, the internal-command queue was the only hop that carried them and the
/// outbox carried none, which is exactly the drift a single helper prevents.
/// </para>
/// </summary>
internal static class AmbientOrigin
{
    /// <summary>
    /// The origin restored by the hop currently running on this async flow, or null outside one.
    /// </summary>
    private static readonly AsyncLocal<OriginSnapshot?> CurrentOrigin = new();

    /// <summary>
    /// Gets the origin the enclosing background hop restored, or null when no hop is running on
    /// this async flow (every HTTP request, and every background service outside a delivery). The
    /// three scoped carriers read it once, at construction, so a scope a handler opens for itself
    /// from the root factory inside the delivery still runs as the original interaction.
    /// </summary>
    internal static OriginSnapshot? Current => CurrentOrigin.Value;

    /// <summary>
    /// Column width of every stored roles list (<c>InternalCommands.UserRoles</c> and
    /// <c>OutboxMessages.UserRoles</c>); a longer list is truncated to fit.
    /// </summary>
    internal const int MaxRolesLength = 512;

    /// <summary>
    /// Flattens the principal's roles to the comma-separated form the row and the message header
    /// both carry. A delimited value keeps the schema free of a child table for what is almost
    /// always one role, and the reader rebuilds them as claims.
    /// </summary>
    /// <remarks>
    /// Null-tolerant even though <c>ICurrentUserService.Roles</c> is declared non-nullable, for the
    /// reason that interface's own remarks give: <c>Roles</c> is a default interface member, so it
    /// runs against every implementation including hand-written doubles and mocks that stub only the
    /// properties a caller touches. A null there must read as "no roles", never as a capture that
    /// throws inside a save.
    /// </remarks>
    /// <param name="roles">The roles held by the capturing principal.</param>
    /// <returns>The comma-separated list truncated to the column width, or null when there are none.</returns>
    internal static string? FlattenRoles(IEnumerable<string>? roles)
    {
        if (roles is null)
        {
            return null;
        }

        string[] materialized = [.. roles];

        if (materialized.Length == 0)
        {
            return null;
        }

        var joined = string.Join(',', materialized);
        return joined.Length <= MaxRolesLength ? joined : joined[..MaxRolesLength];
    }

    /// <summary>
    /// Rebuilds a captured principal: the user id as the standard <c>sub</c> claim and each stored
    /// role as a role claim, which is exactly what <c>ClaimsPrincipalExtensions</c> and the
    /// authorization decorator read. Returns null when nothing was captured, so the execution stays
    /// anonymous rather than inventing an identity.
    /// </summary>
    /// <param name="userId">The captured user id, or null for system-raised work.</param>
    /// <param name="userRoles">The captured roles as a comma-separated list, or null.</param>
    /// <param name="authenticationType">
    /// The authentication type stamped on the rebuilt identity. It is what makes
    /// <c>IsAuthenticated</c> true (without one the principal reads as anonymous no matter how many
    /// claims it carries), and it names the hop the identity came back from.
    /// </param>
    /// <returns>The rebuilt principal, or null when no user was captured.</returns>
    internal static ClaimsPrincipal? BuildPrincipal(
        UserIdentifierType? userId,
        string? userRoles,
        string authenticationType)
    {
        if (userId is not { } id)
        {
            return null;
        }

        List<Claim> claims =
        [
            new Claim(AuthClaimTypes.Subject, id.ToString(CultureInfo.InvariantCulture)),
        ];

        if (userRoles is { Length: > 0 } roles)
        {
            claims.AddRange(roles
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(role => new Claim(ClaimTypes.Role, role)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    /// <summary>
    /// Restores a captured context onto <paramref name="services"/> so the work that follows sees
    /// the identity, tenant and correlation id the original interaction had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Overwrite, never accumulate.</b> The principal is set or cleared on every call, so a scope
    /// reused across several messages cannot let one message's user answer for the next. The
    /// correlation id is likewise overwritten whenever the message carries one.
    /// </para>
    /// <para>
    /// <b>The tenant is the one value that cannot be overwritten.</b> <see cref="ITenantContext"/> is
    /// single-valued for the life of a scope by contract, because a scope whose tenant changed
    /// mid-flight has already read rows under the previous one. It is therefore set only when the
    /// scope has not already resolved a different tenant; a host running database per tenant is
    /// unaffected, since its work is already one scope per tenant.
    /// </para>
    /// <para>
    /// Every service is resolved with <c>GetService</c>. A host that registered
    /// <c>AddInfrastructure</c> has all three, and a bare provider (a directly-constructed test
    /// fixture) simply restores nothing rather than failing the hop.
    /// </para>
    /// <para>
    /// <b>Scopes opened inside the hop inherit it.</b> Integration event handlers are singletons
    /// that open their own scope from the ROOT factory (<c>ScopedIntegrationEventHandlerBase</c>),
    /// a scope that never sees this restore. The restored values are therefore also published as
    /// <see cref="Current"/> on the async flow until the returned handle is disposed, and
    /// <see cref="TenantContext"/>, <see cref="ScopedUserOverride"/> and
    /// <see cref="CorrelationContext"/> seed themselves from it when constructed. The published
    /// tenant and correlation id are the ones the restored scope ends up with, so a scope whose
    /// tenant was already fixed (a database-per-tenant target) hands down that tenant.
    /// </para>
    /// </remarks>
    /// <param name="services">The scope to restore the context onto.</param>
    /// <param name="userId">The captured user id, or null.</param>
    /// <param name="userRoles">The captured roles as a comma-separated list, or null.</param>
    /// <param name="tenantId">The captured tenant, or null.</param>
    /// <param name="correlationId">The captured correlation id, or null.</param>
    /// <param name="authenticationType">The authentication type to stamp on the rebuilt identity.</param>
    /// <returns>
    /// A handle that puts back the previously published origin; dispose it when the hop's work for
    /// this message or row is done, so the next one cannot inherit it.
    /// </returns>
    internal static IDisposable Restore(
        IServiceProvider services,
        UserIdentifierType? userId,
        string? userRoles,
        string? tenantId,
        string? correlationId,
        string authenticationType)
    {
        // Withdraw any enclosing origin while this one is restored: a carrier constructed below
        // belongs to this hop and must not seed itself from the previous one.
        var previous = CurrentOrigin.Value;
        CurrentOrigin.Value = null;

        // Tenant first: it routes the scoped context factory to the right database, and every
        // repository resolved afterwards reads its query filter from it.
        RestoreTenant(services, tenantId);
        RestorePrincipal(services, BuildPrincipal(userId, userRoles, authenticationType));

        var correlationContext = services.GetService<ICorrelationContext>();
        if (correlationId is { Length: > 0 } correlation)
        {
            correlationContext?.SetCorrelationId(correlation);
        }

        CurrentOrigin.Value = new OriginSnapshot(
            userId,
            userRoles,
            services.GetService<ITenantContext>()?.TenantId ?? NullIfEmpty(tenantId),
            correlationContext?.CorrelationId ?? NullIfEmpty(correlationId),
            authenticationType);

        return new RestoreHandle(previous);
    }

    /// <summary>
    /// Sets the captured tenant on the scope unless the scope already resolved a different one
    /// (a tenant cannot change within a scope, see <see cref="ITenantContext"/>).
    /// </summary>
    private static void RestoreTenant(IServiceProvider services, string? tenantId)
    {
        if (tenantId is { Length: > 0 } tenant
            && services.GetService<ITenantContext>() is { } tenantContext
            && (!tenantContext.IsResolved
                || string.Equals(tenantContext.TenantId, tenant, StringComparison.Ordinal)))
        {
            tenantContext.SetTenant(tenant);
        }
    }

    /// <summary>Sets, or clears when nothing was captured, the principal the scope acts as.</summary>
    private static void RestorePrincipal(IServiceProvider services, ClaimsPrincipal? principal)
    {
        if (services.GetService<ScopedUserOverride>() is not { } userOverride)
        {
            return;
        }

        if (principal is null)
        {
            userOverride.Clear();
        }
        else
        {
            userOverride.Set(principal);
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Puts back the origin that was published before a <see cref="Restore"/>.</summary>
    /// <param name="previous">The origin to put back, or null.</param>
    private sealed class RestoreHandle(OriginSnapshot? previous) : IDisposable
    {
        public void Dispose() => CurrentOrigin.Value = previous;
    }
}

/// <summary>The origin a background hop restored, as published on the async flow.</summary>
/// <param name="UserId">The captured user id, or null for system-raised work.</param>
/// <param name="UserRoles">The captured roles as a comma-separated list, or null.</param>
/// <param name="TenantId">The tenant the restored scope runs as, or null.</param>
/// <param name="CorrelationId">The correlation id the restored scope carries, or null.</param>
/// <param name="AuthenticationType">The authentication type stamped on a rebuilt identity.</param>
internal sealed record OriginSnapshot(
    UserIdentifierType? UserId,
    string? UserRoles,
    string? TenantId,
    string? CorrelationId,
    string AuthenticationType)
{
    /// <summary>Rebuilds the captured principal, or null when no user was captured.</summary>
    /// <returns>The principal, or null.</returns>
    public ClaimsPrincipal? BuildPrincipal() => AmbientOrigin.BuildPrincipal(UserId, UserRoles, AuthenticationType);
}
