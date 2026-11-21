using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Namespace applied to every <see cref="MMCA.Common.Application.Interfaces.ICacheService"/> key.
/// <para>
/// Services that share one cache instance also share one keyspace. Nothing stops two of them from
/// choosing the same key for different data, so a read in one service can be served another
/// service's value. Giving each service a prefix (for example <c>"conference:"</c>) separates them.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately applied inside <see cref="HybridCacheService"/> rather than through
/// <c>RedisCacheOptions.InstanceName</c>. <c>InstanceName</c> is prepended by
/// <c>IDistributedCache</c> below our abstraction, where the generation keys prefix invalidation
/// writes directly to Redis (<see cref="CachePrefixGenerations"/>) would not carry it, so two
/// applications on one server would share invalidation tokens while keeping separate entries.
/// Applying the prefix here gives entries, generation keys and distributed locks one key shape.
/// </para>
/// <para>
/// Only the shared stores honor it. <see cref="MemoryCacheService"/> is per-process, so its
/// keyspace is private by construction and a prefix would add nothing.
/// </para>
/// </remarks>
public sealed class CacheKeyPrefixOptions
{
    /// <summary>Configuration section binding to these options.</summary>
    public const string SectionName = "Cache";

    /// <summary>
    /// Prefix prepended to every cache key. Leave it empty to take the framework's per-application
    /// default (<c>"{application namespace}:"</c>, see
    /// <see cref="MMCA.Common.Infrastructure.Configuration.ApplicationNamespace"/>); set it
    /// explicitly only to pin a keyspace two hosts of the same application must share.
    /// </summary>
    /// <remarks>
    /// SEC-Common-53. This used to default to no prefix at all, so two applications sharing one
    /// Redis shared one keyspace for both cache entries and distributed locks, silently.
    /// </remarks>
    public string KeyPrefix { get; init; } = string.Empty;

    /// <summary>
    /// Prefix for the cross-service keys (<see cref="MMCA.Common.Application.Interfaces.CrossServiceCacheKeys"/>),
    /// the records one service writes and another reads, such as the soft-deleted-user marker. Leave it
    /// empty to take the framework default: the token audience (<c>Jwt:Audience</c>), which every service
    /// accepting the same tokens shares and two applications do not, falling back to this host's own
    /// namespace when no audience is configured. Set it only to pin that keyspace explicitly, and set the
    /// SAME value on every service of the deployment.
    /// </summary>
    public string CrossServiceKeyPrefix { get; init; } = string.Empty;
}

/// <summary>Applies <see cref="CacheKeyPrefixOptions.KeyPrefix"/> to cache keys.</summary>
internal sealed class CacheKeyNamespace(string prefix)
{
    /// <summary>The configuration key of the token audience, the default cross-service namespace.</summary>
    private static readonly string AudienceConfigurationKey = string.Concat(Auth.JwtSettings.SectionName, ":", nameof(Auth.JwtSettings.Audience));

    /// <summary>A namespace that leaves keys untouched.</summary>
    public static CacheKeyNamespace None { get; } = new(string.Empty);

    /// <summary>Gets the configured prefix.</summary>
    public string Prefix { get; } = prefix ?? string.Empty;

    /// <summary>Builds the namespace from bound options, tolerating an unregistered section.</summary>
    public static CacheKeyNamespace From(IOptions<CacheKeyPrefixOptions>? options)
    {
        var prefix = options?.Value.KeyPrefix;
        return string.IsNullOrEmpty(prefix) ? None : new CacheKeyNamespace(prefix);
    }

    /// <summary>
    /// Builds the namespace from the container: the configured prefix when a host set one,
    /// otherwise the per-application default derived from
    /// <see cref="Configuration.ApplicationNamespace"/> (SEC-Common-53), so two applications sharing
    /// one Redis never share a keyspace by accident.
    /// </summary>
    /// <param name="serviceProvider">The resolving provider.</param>
    /// <returns>The namespace to qualify cache and lock keys with.</returns>
    public static CacheKeyNamespace From(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var configured = serviceProvider.GetService<IOptions<CacheKeyPrefixOptions>>()?.Value.KeyPrefix;
        if (!string.IsNullOrEmpty(configured))
        {
            return new CacheKeyNamespace(configured);
        }

        var applicationNamespace = Configuration.ApplicationNamespace.Resolve(
            serviceProvider.GetService<IConfiguration>(),
            serviceProvider.GetService<IHostEnvironment>());

        return new CacheKeyNamespace(string.Concat(applicationNamespace, ":"));
    }

    /// <summary>
    /// Builds the namespace the cross-service keys are qualified with
    /// (<see cref="MMCA.Common.Application.Interfaces.CrossServiceCacheKeys"/>): the configured
    /// <see cref="CacheKeyPrefixOptions.CrossServiceKeyPrefix"/>, otherwise the token audience, otherwise
    /// this host's own namespace (<see cref="From(IServiceProvider)"/>).
    /// </summary>
    /// <param name="serviceProvider">The resolving provider.</param>
    /// <returns>The namespace every service of one deployment shares for cross-service keys.</returns>
    /// <remarks>
    /// The audience is the default because it names exactly the set of services that must see a
    /// cross-service record: each service of a deployment is configured with the audience of the tokens
    /// it accepts (while its <c>Application:Namespace</c> and <c>Cache:KeyPrefix</c> differ per service),
    /// and a different application accepts a different audience, so its keys stay apart (SEC-Common-53).
    /// </remarks>
    public static CacheKeyNamespace CrossServiceFrom(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var configured = serviceProvider.GetService<IOptions<CacheKeyPrefixOptions>>()?.Value.CrossServiceKeyPrefix;
        if (!string.IsNullOrEmpty(configured))
        {
            return new CacheKeyNamespace(configured);
        }

        var audience = serviceProvider.GetService<IConfiguration>()?[AudienceConfigurationKey];
        return string.IsNullOrWhiteSpace(audience)
            ? From(serviceProvider)
            : new CacheKeyNamespace(string.Concat(Configuration.ApplicationNamespace.Sanitize(audience), ":"));
    }

    /// <summary>Qualifies a caller-supplied key.</summary>
    public string Qualify(string key) =>
        Prefix.Length == 0 ? key : string.Concat(Prefix, key);
}
