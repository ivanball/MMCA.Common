using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Caching;
using MMCA.Common.Infrastructure.Concurrency;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the cache service. Uses distributed cache (e.g. Redis registered by Aspire)
        /// when available; otherwise falls back to in-memory cache.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddCaching(IConfiguration? configuration = null)
        {
            services.AddMemoryCache();

            // Optional key namespace so services sharing one cache instance cannot collide
            // (Cache:KeyPrefix). Absent configuration leaves keys exactly as callers write them.
            //
            // The same Cache section also carries the TTL policy (CacheSettings) and the one knob the
            // Application-layer query pipeline needs (QueryCachePipelineSettings, defined there
            // because that layer cannot reference this assembly; Infrastructure binds it here so both
            // views read the same keys). Both are registered even without configuration, so
            // IOptions<T> always resolves to the framework defaults rather than failing a host that
            // calls the parameterless overload.
            if (configuration is not null)
            {
                services.Configure<CacheKeyPrefixOptions>(configuration.GetSection(CacheKeyPrefixOptions.SectionName));

                services.AddOptions<CacheSettings>()
                    .Bind(configuration.GetSection(CacheSettings.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                services.AddOptions<Application.Settings.QueryCachePipelineSettings>()
                    .Bind(configuration.GetSection(Application.Settings.QueryCachePipelineSettings.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();
            }
            else
            {
                services.AddOptions<CacheSettings>();
                services.AddOptions<Application.Settings.QueryCachePipelineSettings>();
            }

            services.TryAddSingleton<ICacheService>(sp =>
            {
                var distributedCache = sp.GetService<IDistributedCache>();
                if (distributedCache is not null and not MemoryDistributedCache)
                {
                    var multiplexer = sp.GetService<IConnectionMultiplexer>();
                    var logger = sp.GetService<ILogger<DistributedCacheService>>()
                        ?? NullLogger<DistributedCacheService>.Instance;
                    var keyNamespace = CacheKeyNamespace.From(sp);
                    return new DistributedCacheService(
                        distributedCache,
                        logger,
                        multiplexer,
                        keyNamespace,
                        sp.GetService<IOptions<CacheSettings>>());
                }

                // In-process: the keyspace is private to this process, so no prefix is needed.
                return new MemoryCacheService(
                    sp.GetRequiredService<IMemoryCache>(),
                    sp.GetService<IOptions<CacheSettings>>());
            });

            // Cross-replica mutual exclusion, registered alongside the cache because all three
            // in-framework callers pair the two: the API idempotency filter guards its execute-then-store
            // window, password-reset token redemption guards its read, compare and remove of one cache
            // record, and OAuthControllerBase (when given a lock) guards its read-and-burn of a
            // single-use exchange code. Redis when a multiplexer is
            // registered, process-local (and warn-once) otherwise, mirroring the cache above.
            services.TryAddSingleton<IDistributedLock>(sp =>
            {
                var multiplexer = sp.GetService<IConnectionMultiplexer>();
                if (multiplexer is not null)
                {
                    var redisLogger = sp.GetService<ILogger<RedisDistributedLock>>()
                        ?? NullLogger<RedisDistributedLock>.Instance;
                    var keyNamespace = CacheKeyNamespace.From(sp);
                    return new RedisDistributedLock(multiplexer, redisLogger, keyNamespace);
                }

                var fallbackLogger = sp.GetService<ILogger<InProcessDistributedLock>>()
                    ?? NullLogger<InProcessDistributedLock>.Instance;
                return new InProcessDistributedLock(fallbackLogger);
            });

            return services;
        }

        /// <summary>
        /// Replaces the registered <see cref="ICacheService"/> with the two-level
        /// <see cref="HybridCache"/> implementation: an in-process L1 in front of the host's
        /// distributed cache.
        /// </summary>
        /// <param name="configure">Optional hook to adjust the <see cref="HybridCacheOptions"/> after the framework defaults are applied.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// Opt-in, and intended for hosts that already register a distributed L2 (Redis). A
        /// memory-only host gains nothing: <see cref="MemoryCacheService"/> is already in-process, and
        /// the default registration stays byte-identical to today for every host that does not call
        /// this.
        /// </para>
        /// <para>
        /// Order-independent with <c>AddInfrastructure</c> and
        /// <c>AddCaching</c>, in both directions. Called first, this
        /// registration is already present when <c>AddCaching</c>'s <c>TryAddSingleton</c> runs, so
        /// that one no-ops; called second, <c>RemoveAll</c> drops what <c>AddCaching</c> registered
        /// before adding this one. Either way the host ends with exactly one
        /// <see cref="ICacheService"/> and it is this one.
        /// </para>
        /// <para>
        /// <b>That includes a host's own custom <see cref="ICacheService"/>:</b> <c>RemoveAll</c> does
        /// not distinguish the framework's registration from anyone else's. Calling this is a
        /// statement that the two-level cache is the cache, so a host with a bespoke implementation
        /// should not call it.
        /// </para>
        /// <para>
        /// Cache keys are namespaced exactly as in <c>AddCaching</c> (the <c>Cache:KeyPrefix</c>
        /// section), and the optional <see cref="IConnectionMultiplexer"/> is resolved the same way,
        /// since prefix eviction still needs SCAN.
        /// </para>
        /// </remarks>
        public IServiceCollection AddCommonHybridCache(Action<HybridCacheOptions>? configure = null)
        {
            services.AddHybridCache();

            // Configured through the options pipeline rather than the AddHybridCache callback,
            // because the TTL policy now comes from the bound Cache section and the callback has no
            // service provider to read it from. The host's own hook still runs last, so it can
            // override anything the framework set.
            services.AddOptions<HybridCacheOptions>()
                .Configure<IOptions<CacheSettings>>((options, cacheSettings) =>
                {
                    // Same TTL policy the rest of the framework applies, expressed in HybridCache's
                    // own option type; the local copy is capped so a replica that misses an
                    // invalidation re-reads L2 within the window.
                    var settings = cacheSettings.Value;
                    options.DefaultEntryOptions = new HybridCacheEntryOptions
                    {
                        Expiration = settings.DefaultDuration,
                        LocalCacheExpiration = settings.LocalCacheDuration ?? HybridCacheService.LocalCacheDefault,
                    };

                    configure?.Invoke(options);
                });

            // Deliberately RemoveAll + Add rather than TryAdd: this call is the host stating which
            // cache wins, and it has to win whether it runs before or after AddInfrastructure.
            services.RemoveAll<ICacheService>();
            services.AddSingleton<ICacheService>(sp =>
            {
                var logger = sp.GetService<ILogger<HybridCacheService>>()
                    ?? NullLogger<HybridCacheService>.Instance;
                var multiplexer = sp.GetService<IConnectionMultiplexer>();
                var keyNamespace = CacheKeyNamespace.From(sp);

                return new HybridCacheService(
                    sp.GetRequiredService<HybridCache>(),
                    logger,
                    multiplexer,
                    keyNamespace,
                    sp.GetService<IOptions<CacheSettings>>());
            });

            return services;
        }

        /// <summary>
        /// Calls <see cref="AddCommonHybridCache"/> only when the Redis connection string is
        /// configured, and otherwise leaves the host's cache registration untouched.
        /// </summary>
        /// <remarks>
        /// <see cref="AddCommonHybridCache"/> has no guard of its own, and with no Redis L2 the hybrid
        /// tier degrades to a plain in-memory cache, which is exactly what the
        /// <see cref="MemoryCacheService"/> fallback already provides. Pair it with
        /// <c>AddRedisCaching()</c> (MMCA.Common.Aspire), which registers the distributed cache and
        /// the <see cref="IConnectionMultiplexer"/> the hybrid tier evicts through under the same
        /// connection name. Order-independent with <c>AddInfrastructure</c>, as
        /// <see cref="AddCommonHybridCache"/> is.
        /// </remarks>
        /// <param name="configuration">The host configuration holding the connection string.</param>
        /// <param name="connectionName">The connection-string name of the Redis resource. Defaults to <c>redis</c>, the name every MMCA host uses.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddCommonHybridCacheWhenRedisConfigured(
            IConfiguration configuration,
            string connectionName = "redis")
        {
            ArgumentNullException.ThrowIfNull(configuration);

            if (!string.IsNullOrEmpty(configuration.GetConnectionString(connectionName)))
            {
                services.AddCommonHybridCache();
            }

            return services;
        }
    }
}
