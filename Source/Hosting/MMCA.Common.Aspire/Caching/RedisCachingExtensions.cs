using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Aspire.Caching;

/// <summary>
/// The framework-owned way to wire a host to Redis. Service hosts call these instead of Aspire's
/// <c>AddRedisDistributedCache</c> / <c>AddRedisClient</c> / <c>AddStackExchangeRedisOutputCache</c>
/// directly, because the raw Aspire integrations register a health check that is fatal to readiness.
/// <para>
/// Why this wrapper exists (production incident, 2026-09-02). Aspire's Redis integrations register
/// the <c>AspNetCore.HealthChecks.Redis</c> check under the name <c>StackExchange.Redis</c> with NO
/// tags. The readiness endpoint from <c>MapDefaultEndpoints()</c> includes every check that is not
/// tagged <c>live</c> or <c>optional</c>, so an untagged check silently gates readiness. That check
/// issues <c>CLUSTER INFO</c> whenever the client detects a clustered server, and Azure Managed Redis
/// (Enterprise tier, port 10000) is detected as a cluster by StackExchange.Redis 3.x, which refuses
/// the command without admin mode. Every probe therefore threw, every replica reported not ready,
/// and the platform stopped routing traffic the applications were perfectly able to serve.
/// </para>
/// <para>
/// The fix is structural rather than a tag patch on someone else's registration: the Aspire checks
/// are switched off at the source (<c>DisableHealthChecks</c>) and Common contributes its own
/// PING-only check, named <c>redis</c> and tagged <see cref="HealthCheckTags.Optional"/>, from
/// <c>AddInfrastructureHealthChecks()</c>. A cache the application degrades gracefully without must
/// be visible on <c>/health</c> and invisible to <c>/health/ready</c>.
/// </para>
/// </summary>
public static class RedisCachingExtensions
{
    /// <summary>
    /// The connection-string name every MMCA host uses for its Redis resource.
    /// </summary>
    public const string DefaultConnectionName = "redis";

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Registers the Redis distributed cache and the <c>IConnectionMultiplexer</c> client when the
        /// named connection string is configured, with the Aspire health checks disabled so nothing
        /// untagged reaches readiness.
        /// <para>
        /// A no-op when the connection string is absent or blank: Redis is optional per host, and the
        /// in-memory cache fallback is the documented behavior for local runs and tests. The client is
        /// registered alongside the cache on purpose: <c>DistributedCacheService</c> needs an
        /// <c>IConnectionMultiplexer</c> for SCAN-based prefix eviction, and without it every
        /// <c>ICacheInvalidating</c> command's prefix invalidation degrades to a silent no-op bounded
        /// only by TTL.
        /// </para>
        /// </summary>
        /// <param name="connectionName">
        /// The connection-string name of the Redis resource. Defaults to
        /// <see cref="DefaultConnectionName"/>.
        /// </param>
        /// <returns>The same builder instance for chaining.</returns>
        public TBuilder AddRedisCaching(string connectionName = DefaultConnectionName)
        {
            if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(connectionName)))
            {
                return builder;
            }

            builder.AddRedisDistributedCache(connectionName, settings => settings.DisableHealthChecks = true);
            builder.AddRedisClient(connectionName, settings => settings.DisableHealthChecks = true);

            return builder;
        }

        /// <summary>
        /// Backs the ASP.NET Core output cache with the same Redis instance when the named connection
        /// string is configured, so tag eviction crosses replicas instead of reaching only the replica
        /// that served the mutation.
        /// <para>
        /// A no-op when the connection string is absent: the built-in per-replica memory store still
        /// applies, which is correct at a single replica. Call this BEFORE <c>AddOutputCache(...)</c>;
        /// that call registers its store with <c>TryAdd</c>, so the explicit Redis store registered
        /// here wins either way, but keeping the order explicit documents the intent.
        /// </para>
        /// <para>
        /// This integration registers no health check of its own, so there is nothing to disable here.
        /// The PING check contributed by <c>AddInfrastructureHealthChecks()</c> already covers
        /// reachability for the whole Redis resource.
        /// </para>
        /// </summary>
        /// <param name="connectionName">
        /// The connection-string name of the Redis resource. Defaults to
        /// <see cref="DefaultConnectionName"/>.
        /// </param>
        /// <returns>The same builder instance for chaining.</returns>
        public TBuilder AddRedisOutputCaching(string connectionName = DefaultConnectionName)
        {
            var connectionString = builder.Configuration.GetConnectionString(connectionName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return builder;
            }

            builder.Services.AddStackExchangeRedisOutputCache(options => options.Configuration = connectionString);

            return builder;
        }
    }
}
