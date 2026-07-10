using Microsoft.AspNetCore.OutputCaching;

namespace MMCA.Common.API.Caching;

/// <summary>Registration helpers for the framework's output-cache policies.</summary>
public static class OutputCacheOptionsExtensions
{
    extension(OutputCacheOptions options)
    {
        /// <summary>
        /// Registers a named policy backed by <see cref="PublicEndpointOutputCachePolicy"/>:
        /// caches GET/HEAD responses for the given duration with the given eviction tags,
        /// regardless of whether the request carries an <c>Authorization</c> header.
        /// Apply ONLY to <c>[AllowAnonymous]</c> endpoints whose payload is identical for
        /// every caller; see the policy's security remarks.
        /// </summary>
        /// <param name="name">The policy name referenced by <c>[OutputCache(PolicyName = ...)]</c>.</param>
        /// <param name="expiration">How long a cached response stays valid.</param>
        /// <param name="tags">Tags for targeted eviction.</param>
        public void AddPublicEndpointPolicy(string name, TimeSpan expiration, params string[] tags)
            => options.AddPolicy(name, new PublicEndpointOutputCachePolicy(expiration, tags));

        /// <summary>
        /// Registers a named policy backed by <see cref="PublicEndpointOutputCachePolicy"/>
        /// with cache-bypassing roles: callers in a bypass role skip the cache entirely
        /// (no lookup, no storage) and always read fresh. Use for endpoints whose payload is
        /// identical for every caller EXCEPT a privileged role that receives an elevated
        /// payload (e.g. organizers see unpublished rows); see the policy's security remarks.
        /// </summary>
        /// <param name="name">The policy name referenced by <c>[OutputCache(PolicyName = ...)]</c>.</param>
        /// <param name="expiration">How long a cached response stays valid.</param>
        /// <param name="bypassRoles">Roles whose callers skip the cache entirely.</param>
        /// <param name="tags">Tags for targeted eviction.</param>
        public void AddPublicEndpointPolicy(string name, TimeSpan expiration, string[] bypassRoles, params string[] tags)
            => options.AddPolicy(name, new PublicEndpointOutputCachePolicy(expiration, bypassRoles, tags));
    }
}
