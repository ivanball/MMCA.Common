using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace MMCA.Common.Gateway.Configuration;

/// <summary>
/// Resolves each cluster's forwarder request profile from three sources.
/// <para>
/// Precedence, per property rather than per block, so a cluster that states one value keeps
/// inheriting the rest: the cluster's own <c>HttpRequest</c> in the <c>ReverseProxy</c> section,
/// then <c>ClusterRequestOverrides[clusterId]</c>, then <c>ClusterRequestDefaults</c>. That ordering
/// is the point of the type: a gateway fronting services that all speak h2c on cleartext declares
/// the version pair and the activity timeout once instead of repeating an identical
/// <c>HttpRequest</c> block per cluster, while the one cluster that genuinely differs (a long-lived
/// WebSocket hub, an HTTP/1.1-only head) still says so locally.
/// </para>
/// <para>
/// A resolved HTTP/2 version pair is forwarded as stated. Dropping a cluster to HTTP/1.1 is a
/// per-cluster decision expressed in its own profile, so the resolved values reach the forwarder
/// exactly as configuration declares them.
/// </para>
/// </summary>
/// <param name="options">The bound gateway settings.</param>
public sealed class GatewayClusterProfileConfigFilter(IOptions<GatewaySettings> options) : IProxyConfigFilter
{
    private readonly GatewaySettings _settings = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Applies the resolved request profile to <paramref name="cluster"/>.</summary>
    /// <param name="cluster">The cluster as loaded from configuration.</param>
    /// <param name="cancel">The cancellation token.</param>
    /// <returns>The cluster to use.</returns>
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        var resolved = Resolve(cluster);

        return ValueTask.FromResult(
            IsSameAs(cluster.HttpRequest, resolved)
                ? cluster
                : cluster with { HttpRequest = resolved });
    }

    /// <summary>Routes carry no request profile; only clusters do.</summary>
    /// <param name="route">The route as loaded from configuration.</param>
    /// <param name="cluster">The cluster the route targets, when it resolves.</param>
    /// <param name="cancel">The cancellation token.</param>
    /// <returns>The route unchanged.</returns>
    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel) =>
        ValueTask.FromResult(route);

    /// <summary>
    /// Merges the three sources for one cluster.
    /// </summary>
    /// <param name="cluster">The cluster as loaded from configuration.</param>
    /// <returns>The forwarder request config the cluster should carry.</returns>
    /// <remarks>Internal so the precedence rules are unit-testable through the filter's own type.</remarks>
    internal ForwarderRequestConfig Resolve(ClusterConfig cluster)
    {
        var clusterId = cluster.ClusterId;
        var own = cluster.HttpRequest;

        _settings.ClusterRequestOverrides.TryGetValue(clusterId, out var over);
        var fallback = _settings.ClusterRequestDefaults;

        return new ForwarderRequestConfig
        {
            Version = ResolveVersion(clusterId, own, over, fallback),
            VersionPolicy = ResolveVersionPolicy(clusterId, own, over, fallback),
            ActivityTimeout = ResolveActivityTimeout(own, over, fallback),
            AllowResponseBuffering = ResolveAllowResponseBuffering(own, over, fallback),
        };
    }

    /// <summary>Most-specific-wins resolution of the HTTP version.</summary>
    /// <param name="clusterId">Cluster id, used only in failure messages.</param>
    /// <param name="own">The cluster's own profile.</param>
    /// <param name="over">The per-cluster override, if any.</param>
    /// <param name="fallback">The shared defaults, if any.</param>
    /// <returns>The resolved version, or <see langword="null"/> when no source states one.</returns>
    private static Version? ResolveVersion(
        string clusterId,
        ForwarderRequestConfig? own,
        GatewayClusterRequestProfile? over,
        GatewayClusterRequestProfile? fallback) =>
        own?.Version ?? over?.ParseVersion(clusterId) ?? fallback?.ParseVersion(clusterId);

    /// <summary>Most-specific-wins resolution of the version policy.</summary>
    /// <param name="clusterId">Cluster id, used only in failure messages.</param>
    /// <param name="own">The cluster's own profile.</param>
    /// <param name="over">The per-cluster override, if any.</param>
    /// <param name="fallback">The shared defaults, if any.</param>
    /// <returns>The resolved policy, or <see langword="null"/> when no source states one.</returns>
    private static HttpVersionPolicy? ResolveVersionPolicy(
        string clusterId,
        ForwarderRequestConfig? own,
        GatewayClusterRequestProfile? over,
        GatewayClusterRequestProfile? fallback) =>
        own?.VersionPolicy ?? over?.ParseVersionPolicy(clusterId) ?? fallback?.ParseVersionPolicy(clusterId);

    /// <summary>Most-specific-wins resolution of the forwarder activity timeout.</summary>
    /// <param name="own">The cluster's own profile.</param>
    /// <param name="over">The per-cluster override, if any.</param>
    /// <param name="fallback">The shared defaults, if any.</param>
    /// <returns>The resolved timeout, or <see langword="null"/> when no source states one.</returns>
    private static TimeSpan? ResolveActivityTimeout(
        ForwarderRequestConfig? own,
        GatewayClusterRequestProfile? over,
        GatewayClusterRequestProfile? fallback) =>
        own?.ActivityTimeout ?? over?.ActivityTimeout ?? fallback?.ActivityTimeout;

    /// <summary>Most-specific-wins resolution of the response-buffering flag.</summary>
    /// <param name="own">The cluster's own profile.</param>
    /// <param name="over">The per-cluster override, if any.</param>
    /// <param name="fallback">The shared defaults, if any.</param>
    /// <returns>The resolved flag, or <see langword="null"/> when no source states one.</returns>
    private static bool? ResolveAllowResponseBuffering(
        ForwarderRequestConfig? own,
        GatewayClusterRequestProfile? over,
        GatewayClusterRequestProfile? fallback) =>
        own?.AllowResponseBuffering ?? over?.AllowResponseBuffering ?? fallback?.AllowResponseBuffering;

    /// <summary>
    /// Whether the resolved profile is the one the cluster already carries, so an unchanged cluster
    /// is returned by reference and YARP's config-change detection sees no churn on every reload.
    /// </summary>
    /// <param name="existing">The cluster's current profile.</param>
    /// <param name="resolved">The freshly resolved profile.</param>
    /// <returns><see langword="true"/> when the two are equivalent.</returns>
    private static bool IsSameAs(ForwarderRequestConfig? existing, ForwarderRequestConfig resolved)
    {
        // An absent block and an all-null block mean the same thing to the forwarder, so a cluster
        // that had no profile and gained no defaults must come back untouched rather than acquiring
        // an empty one.
        var current = existing ?? ForwarderRequestConfig.Empty;

        return current.Version == resolved.Version
            && current.VersionPolicy == resolved.VersionPolicy
            && current.ActivityTimeout == resolved.ActivityTimeout
            && current.AllowResponseBuffering == resolved.AllowResponseBuffering;
    }
}
