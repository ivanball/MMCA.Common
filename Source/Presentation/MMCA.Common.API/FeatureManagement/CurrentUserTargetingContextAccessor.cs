using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.FeatureManagement.FeatureFilters;

namespace MMCA.Common.API.FeatureManagement;

/// <summary>
/// Supplies the targeting context (user id plus group membership) that the feature-management
/// <c>Targeting</c> filter evaluates, read from the current HTTP request's principal. Registered by
/// <c>AddAPI</c> via <c>AddFeatureManagement().WithTargeting&lt;CurrentUserTargetingContextAccessor&gt;()</c>,
/// which turns a feature flag into a percentage rollout that is <b>sticky per user</b> rather than
/// random per request: the filter hashes the user id, so the same caller keeps the same answer
/// across requests and instances.
/// <para>
/// The user id is the <c>user_id</c> claim emitted by <c>TokenService</c> (the same claim
/// <c>CurrentUserService</c> and <c>IdempotencyFilter</c> read), falling back to the principal's
/// name when a token predates it. Groups are the caller's role claims, accepting each claim type
/// the JWT middleware may produce: the standard <see cref="ClaimTypes.Role"/> URI when inbound
/// claim mapping is on, or the raw <c>role</c> / <c>roles</c> claim when it is off. That mirrors
/// <c>ICurrentUserService.Roles</c>, which cannot be used directly here because the accessor is
/// registered as a singleton while <c>ICurrentUserService</c> is scoped.
/// </para>
/// <para>
/// An anonymous request yields an empty context (no user id, no groups), so a targeted feature is
/// simply off for anonymous callers unless the audience opts everyone in.
/// </para>
/// <example>
/// A rollout that always includes the Organizer role, includes 25 percent of everyone else, and
/// pins two named users:
/// <code>
/// "FeatureManagement": {
///   "Conference.NewAgenda": {
///     "EnabledFor": [
///       {
///         "Name": "Targeting",
///         "Parameters": {
///           "Audience": {
///             "Users": [ "42", "1337" ],
///             "Groups": [ { "Name": "Organizer", "RolloutPercentage": 100 } ],
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
    /// <summary>The claim type carrying the user identifier, matching <c>TokenService</c>.</summary>
    private const string UserIdClaimType = "user_id";

    /// <summary>
    /// Builds the targeting context for the current request. Never returns <see langword="null"/>:
    /// a request without a principal (background work, an anonymous call) produces an empty context
    /// rather than an error, because a feature filter must not be able to fail a request.
    /// </summary>
    /// <returns>The targeting context for the current caller.</returns>
    public ValueTask<TargetingContext> GetContextAsync()
    {
        var user = httpContextAccessor.HttpContext?.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult(new TargetingContext
            {
                UserId = null,
                Groups = [],
            });
        }

        var groups = user.Claims
            .Where(claim =>
                string.Equals(claim.Type, ClaimTypes.Role, StringComparison.Ordinal)
                || string.Equals(claim.Type, "role", StringComparison.Ordinal)
                || string.Equals(claim.Type, "roles", StringComparison.Ordinal))
            .Select(claim => claim.Value)
            .ToArray();

        return ValueTask.FromResult(new TargetingContext
        {
            UserId = user.FindFirst(UserIdClaimType)?.Value ?? user.Identity.Name,
            Groups = groups,
        });
    }
}
