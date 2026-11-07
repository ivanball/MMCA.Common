using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement.FeatureFilters;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.FeatureManagement;

/// <summary>
/// Supplies the targeting context (user id plus group membership) that the feature-management
/// <c>Targeting</c> filter evaluates, read from the current HTTP request's principal. Registered by
/// <c>AddAPI</c> via <c>AddFeatureManagement().WithTargeting&lt;CurrentUserTargetingContextAccessor&gt;()</c>,
/// which turns a feature flag into a percentage rollout that is <b>sticky per user</b> rather than
/// random per request: the filter hashes the user id, so the same caller keeps the same answer
/// across requests and instances.
/// <para>
/// The user id is the standard <c>sub</c> claim <c>TokenService</c> emits, read through
/// <c>ClaimsPrincipalExtensions</c> so the <see cref="ClaimTypes.NameIdentifier"/> form the bearer
/// handler maps it to resolves identically (the same read <c>CurrentUserService</c> and
/// <c>IdempotencyFilter</c> perform), falling back to the principal's name when a token carries
/// neither. Groups are the caller's role claims, accepting each claim type
/// the JWT middleware may produce: the standard <see cref="ClaimTypes.Role"/> URI when inbound
/// claim mapping is on, or the raw <c>role</c> / <c>roles</c> claim when it is off. That mirrors
/// <c>ICurrentUserService.Roles</c>, which cannot be used directly here because the accessor is
/// registered as a singleton while <c>ICurrentUserService</c> is scoped.
/// </para>
/// <para>
/// <b>Tenant targeting.</b> When the request's <see cref="ITenantContext"/> has resolved a tenant
/// (ADR-073), the groups also carry <c>tenant:&lt;tenantId&gt;</c> (see <see cref="TenantGroup"/>),
/// so an audience group such as <c>tenant:acme</c> turns a flag on for one tenant. The tenant
/// context is scoped, so it is read from the request's own service provider. A host without
/// tenancy never resolves a tenant and gets no extra group, and the group only matters to an
/// audience that names it, so a host that targets nothing by tenant sees no change.
/// </para>
/// <para>
/// An anonymous request yields an empty context (no user id, no groups beyond the resolved
/// tenant's), so a targeted feature is simply off for anonymous callers unless the audience opts
/// everyone in or names their tenant.
/// </para>
/// <example>
/// A rollout that always includes one of the host's own roles and every request of tenant
/// <c>acme</c>, includes 25 percent of everyone else, and pins two named users:
/// <code>
/// "FeatureManagement": {
///   "Conference.NewAgenda": {
///     "EnabledFor": [
///       {
///         "Name": "Targeting",
///         "Parameters": {
///           "Audience": {
///             "Users": [ "42", "1337" ],
///             "Groups": [
///               { "Name": "Reviewers", "RolloutPercentage": 100 },
///               { "Name": "tenant:acme", "RolloutPercentage": 100 }
///             ],
///             "DefaultRolloutPercentage": 25
///           }
///         }
///       }
///     ]
///   }
/// }
/// </code>
/// </example>
/// </summary>
/// <param name="httpContextAccessor">Accessor for the current request, or none outside a request.</param>
public sealed class CurrentUserTargetingContextAccessor(IHttpContextAccessor httpContextAccessor)
    : ITargetingContextAccessor
{
    /// <summary>
    /// The prefix of the targeting group that carries the resolved tenant: an audience group named
    /// <c>tenant:acme</c> targets every request resolved to tenant <c>acme</c>.
    /// </summary>
    public const string TenantGroupPrefix = "tenant:";

    /// <summary>
    /// Builds the targeting group name for <paramref name="tenantId"/>, exactly as
    /// <see cref="GetContextAsync"/> emits it.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <returns><c>tenant:&lt;tenantId&gt;</c>.</returns>
    public static string TenantGroup(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return TenantGroupPrefix + tenantId;
    }

    /// <summary>
    /// Builds the targeting context for the current request. Never returns <see langword="null"/>:
    /// a request without a principal (background work, an anonymous call) produces an empty context
    /// rather than an error, because a feature filter must not be able to fail a request.
    /// </summary>
    /// <returns>The targeting context for the current caller.</returns>
    public ValueTask<TargetingContext> GetContextAsync()
    {
        var httpContext = httpContextAccessor.HttpContext;
        var user = httpContext?.User;
        var tenantGroups = ResolveTenantGroups(httpContext);

        if (user?.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult(new TargetingContext
            {
                UserId = null,
                Groups = tenantGroups,
            });
        }

        var groups = user.Claims
            .Where(claim =>
                string.Equals(claim.Type, ClaimTypes.Role, StringComparison.Ordinal)
                || string.Equals(claim.Type, "role", StringComparison.Ordinal)
                || string.Equals(claim.Type, "roles", StringComparison.Ordinal))
            .Select(claim => claim.Value)
            .Concat(tenantGroups)
            .ToArray();

        return ValueTask.FromResult(new TargetingContext
        {
            UserId = user.FindUserIdValue() ?? user.Identity.Name,
            Groups = groups,
        });
    }

    // ITenantContext is scoped, so it comes from the request's own provider; a host that never
    // registered it, or a request that resolved no tenant, contributes no group.
    private static string[] ResolveTenantGroups(HttpContext? httpContext)
    {
        var tenantId = httpContext?.RequestServices?.GetService<ITenantContext>()?.TenantId;
        return string.IsNullOrWhiteSpace(tenantId) ? [] : [TenantGroup(tenantId)];
    }
}
