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
    /// </remarks>
    /// <param name="services">The scope to restore the context onto.</param>
    /// <param name="userId">The captured user id, or null.</param>
    /// <param name="userRoles">The captured roles as a comma-separated list, or null.</param>
    /// <param name="tenantId">The captured tenant, or null.</param>
    /// <param name="correlationId">The captured correlation id, or null.</param>
    /// <param name="authenticationType">The authentication type to stamp on the rebuilt identity.</param>
    internal static void Restore(
        IServiceProvider services,
        UserIdentifierType? userId,
        string? userRoles,
        string? tenantId,
        string? correlationId,
        string authenticationType)
    {
        // Tenant first: it routes the scoped context factory to the right database, and every
        // repository resolved afterwards reads its query filter from it.
        if (tenantId is { Length: > 0 } tenant
            && services.GetService<ITenantContext>() is { } tenantContext
            && (!tenantContext.IsResolved
                || string.Equals(tenantContext.TenantId, tenant, StringComparison.Ordinal)))
        {
            tenantContext.SetTenant(tenant);
        }

        if (services.GetService<ScopedUserOverride>() is { } userOverride)
        {
            var principal = BuildPrincipal(userId, userRoles, authenticationType);
            if (principal is null)
            {
                userOverride.Clear();
            }
            else
            {
                userOverride.Set(principal);
            }
        }

        if (correlationId is { Length: > 0 } correlation)
        {
            services.GetService<ICorrelationContext>()?.SetCorrelationId(correlation);
        }
    }
}
