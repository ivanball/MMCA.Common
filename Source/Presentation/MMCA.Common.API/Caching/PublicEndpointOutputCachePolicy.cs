using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;

namespace MMCA.Common.API.Caching;

/// <summary>
/// Output-cache policy for public, user-independent GET endpoints that must stay cacheable
/// even when the request carries an <c>Authorization</c> header.
/// <para>
/// The framework's UI attaches a Bearer token to every outgoing API request, including reads
/// of <c>[AllowAnonymous]</c> endpoints whose payload is identical for every caller. The
/// built-in default policy refuses to serve or store cached responses for any request with
/// an <c>Authorization</c> header, so for logged-in users those endpoints bypass the output
/// cache entirely and every read lands on the database. This policy replaces the default for
/// such endpoints: it caches GET/HEAD responses regardless of the caller's auth state.
/// </para>
/// <para>
/// Like the built-in default policy, the cache key varies by every query-string parameter
/// (<c>CacheVaryByRules.QueryKeys = "*"</c>) — search, paging, filtering, and field-projection
/// variants of the same path each get their own entry.
/// </para>
/// <para>
/// SECURITY: apply this policy ONLY to endpoints whose response does not depend on the
/// caller's identity — a cached response is served verbatim to every subsequent caller.
/// When an endpoint returns an elevated payload for a privileged role (e.g. organizers see
/// unpublished rows), pass that role via <c>bypassRoles</c>: callers in a bypass
/// role skip the cache entirely (no lookup, no storage), so their elevated responses are
/// never cached and they always read fresh, while every other caller's identical payload
/// stays cacheable. Responses that set cookies or return non-200 status codes are never
/// stored.
/// </para>
/// </summary>
public sealed class PublicEndpointOutputCachePolicy : IOutputCachePolicy
{
    private readonly TimeSpan _expiration;
    private readonly string[] _bypassRoles;
    private readonly string[] _tags;

    /// <summary>Initializes the policy with an expiration and invalidation tags.</summary>
    /// <param name="expiration">How long a cached response stays valid.</param>
    /// <param name="tags">Tags for targeted eviction via <c>IOutputCacheStore.EvictByTagAsync</c>.</param>
    public PublicEndpointOutputCachePolicy(TimeSpan expiration, params string[] tags)
        : this(expiration, [], tags)
    {
    }

    /// <summary>Initializes the policy with an expiration, cache-bypassing roles, and invalidation tags.</summary>
    /// <param name="expiration">How long a cached response stays valid.</param>
    /// <param name="bypassRoles">Roles whose callers skip the cache entirely (no lookup, no storage) —
    /// use for roles that receive an elevated, identity-dependent payload from the endpoint.</param>
    /// <param name="tags">Tags for targeted eviction via <c>IOutputCacheStore.EvictByTagAsync</c>.</param>
    public PublicEndpointOutputCachePolicy(TimeSpan expiration, string[] bypassRoles, string[] tags)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiration, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(bypassRoles);
        ArgumentNullException.ThrowIfNull(tags);

        _expiration = expiration;
        _bypassRoles = bypassRoles;
        _tags = tags;
    }

    /// <inheritdoc />
    ValueTask IOutputCachePolicy.CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        // Mirrors the built-in default policy MINUS the Authorization-header / authenticated-
        // identity bail-out: the response is user-independent by contract (see class docs).
        // Callers in a bypass role get the default-policy behavior back: no lookup, no storage.
        var attemptOutputCaching = IsCacheableRequest(context.HttpContext.Request)
            && !IsBypassedCaller(context.HttpContext.User);
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = attemptOutputCaching;
        context.AllowCacheStorage = attemptOutputCaching;
        context.AllowLocking = true;
        context.ResponseExpirationTimeSpan = _expiration;

        // Same rule as the built-in default policy: every query-string variant of the path
        // (search, paging, filters, field projections) is its own cache entry.
        context.CacheVaryByRules.QueryKeys = "*";

        foreach (var tag in _tags)
            context.Tags.Add(tag);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    ValueTask IOutputCachePolicy.ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    ValueTask IOutputCachePolicy.ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var response = context.HttpContext.Response;

        // Never store responses that set cookies or are not plain 200s (same rule as the
        // built-in default policy).
        if (!StringValues.IsNullOrEmpty(response.Headers.SetCookie)
            || response.StatusCode != StatusCodes.Status200OK)
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsCacheableRequest(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);

    private bool IsBypassedCaller(ClaimsPrincipal user) =>
        Array.Exists(_bypassRoles, user.IsInRole);
}
