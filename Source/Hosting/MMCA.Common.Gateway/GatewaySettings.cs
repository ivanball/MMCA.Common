using System.ComponentModel.DataAnnotations;
using System.Net;

namespace MMCA.Common.Gateway;

/// <summary>
/// The whole configuration surface of <c>AddMmcaGateway</c>, bound from the <c>MmcaGateway</c>
/// section. Nothing here duplicates the YARP <c>ReverseProxy</c> section: routes, clusters and
/// destinations stay where they are, and this section only carries the cross-cutting defaults a
/// gateway would otherwise copy into every cluster by hand.
/// </summary>
public sealed class GatewaySettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MmcaGateway";

    /// <summary>
    /// The request profile applied to every cluster that does not state a value of its own. This is
    /// the copy-paste eliminator: the shared activity timeout and the h2c version pair are declared
    /// once here instead of in each cluster's <c>HttpRequest</c> block.
    /// </summary>
    public GatewayClusterRequestProfile? ClusterRequestDefaults { get; init; }

    /// <summary>
    /// Per-cluster overrides of <see cref="ClusterRequestDefaults"/>, keyed by cluster id. A cluster
    /// whose profile genuinely differs (a long-lived WebSocket hub, an HTTP/1.1-only head) states
    /// only the properties that differ; the rest still come from the defaults.
    /// </summary>
    public IReadOnlyDictionary<string, GatewayClusterRequestProfile> ClusterRequestOverrides { get; init; }
        = new Dictionary<string, GatewayClusterRequestProfile>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Destination health-check defaults applied to clusters that declare none.</summary>
    public GatewayHealthCheckDefaults HealthCheckDefaults { get; init; } = new();

    /// <summary>Route/cluster trace headers stamped onto every proxied request.</summary>
    public GatewayTraceHeaderSettings TraceHeaders { get; init; } = new();

    /// <summary>
    /// Named rate-limiter policies, keyed by the name a route references through YARP's own
    /// <c>RateLimiterPolicy</c> property.
    /// </summary>
    public IReadOnlyDictionary<string, GatewayRoutePolicySettings> RateLimiterPolicies { get; init; }
        = new Dictionary<string, GatewayRoutePolicySettings>(StringComparer.Ordinal);
}

/// <summary>
/// A YARP forwarder request profile expressed in configuration-friendly primitives. The version
/// pair is text rather than <see cref="System.Version"/> / <see cref="HttpVersionPolicy"/> so a
/// mistyped value fails at startup with a message naming the cluster, instead of binding to a
/// silent default.
/// </summary>
public sealed class GatewayClusterRequestProfile
{
    /// <summary>
    /// HTTP version, e.g. <c>"2.0"</c> or <c>"1.1"</c>. <see langword="null"/> leaves the property
    /// to a lower-precedence source.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Version policy name, e.g. <c>"RequestVersionExact"</c> (h2c prior knowledge) or
    /// <c>"RequestVersionOrLower"</c>. <see langword="null"/> defers to a lower-precedence source.
    /// </summary>
    public string? VersionPolicy { get; init; }

    /// <summary>How long a forwarded request may stay idle before the forwarder aborts it.</summary>
    public TimeSpan? ActivityTimeout { get; init; }

    /// <summary>Whether the forwarder may buffer the response body.</summary>
    public bool? AllowResponseBuffering { get; init; }

    /// <summary>Parses <see cref="Version"/>, or <see langword="null"/> when unset.</summary>
    /// <param name="clusterId">Cluster id, used only in the failure message.</param>
    /// <returns>The parsed version.</returns>
    /// <exception cref="InvalidOperationException">The value is not a valid version.</exception>
    internal Version? ParseVersion(string clusterId)
    {
        if (Version is null)
        {
            return null;
        }

        if (!System.Version.TryParse(Version, out var parsed))
        {
            throw new InvalidOperationException(
                $"Cluster '{clusterId}': '{Version}' is not a valid HTTP version. Use \"2.0\" or \"1.1\".");
        }

        return parsed;
    }

    /// <summary>Parses <see cref="VersionPolicy"/>, or <see langword="null"/> when unset.</summary>
    /// <param name="clusterId">Cluster id, used only in the failure message.</param>
    /// <returns>The parsed policy.</returns>
    /// <exception cref="InvalidOperationException">The value is not a valid policy name.</exception>
    internal HttpVersionPolicy? ParseVersionPolicy(string clusterId)
    {
        if (VersionPolicy is null)
        {
            return null;
        }

        if (!Enum.TryParse<HttpVersionPolicy>(VersionPolicy, ignoreCase: true, out var parsed))
        {
            throw new InvalidOperationException(
                $"Cluster '{clusterId}': '{VersionPolicy}' is not a valid HttpVersionPolicy. "
                + "Use RequestVersionExact, RequestVersionOrLower or RequestVersionOrHigher.");
        }

        return parsed;
    }
}

/// <summary>
/// Destination health-check defaults. YARP ejects a failing destination only when a cluster carries
/// a health-check block, and a gateway assembled from configuration routinely ships without one, so
/// these defaults turn ejection on for every cluster that stayed silent.
/// </summary>
public sealed class GatewayHealthCheckDefaults
{
    /// <summary>Passive (in-band) defaults. Applied only to clusters with no passive block.</summary>
    public GatewayPassiveHealthCheckDefaults Passive { get; init; } = new();

    /// <summary>Active (out-of-band probe) defaults. Off unless the host turns them on.</summary>
    public GatewayActiveHealthCheckDefaults Active { get; init; } = new();
}

/// <summary>
/// Passive health-check defaults: YARP watches the forwarded responses it is already making, so
/// this costs no extra traffic, which is why it is the default rather than active probing.
/// </summary>
public sealed class GatewayPassiveHealthCheckDefaults
{
    /// <summary>Whether to apply a passive block to clusters that declare none.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The YARP passive policy name. <c>TransportFailureRate</c> is built in.</summary>
    [Required]
    public string Policy { get; init; } = "TransportFailureRate";

    /// <summary>How long an ejected destination stays out before it is retried.</summary>
    public TimeSpan ReactivationPeriod { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Active health-check defaults. Off by default: an extra probe per destination per interval is
/// real traffic and real cost, and passive checks already eject a destination that is failing the
/// requests the gateway actually cares about.
/// </summary>
public sealed class GatewayActiveHealthCheckDefaults
{
    /// <summary>Whether to apply an active block to clusters that declare none.</summary>
    public bool Enabled { get; init; }

    /// <summary>The YARP active policy name. <c>ConsecutiveFailures</c> is built in.</summary>
    [Required]
    public string Policy { get; init; } = "ConsecutiveFailures";

    /// <summary>Probe interval.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Per-probe budget.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Probe path. <c>/alive</c> rather than <c>/health</c> on purpose: readiness on a downstream
    /// flips during its own rolling deployment, and ejecting a destination for that is the gateway
    /// reacting to a healthy deployment as if it were an outage.
    /// </summary>
    [Required]
    public string Path { get; init; } = "/alive";
}

/// <summary>
/// The route/cluster trace headers stamped onto each proxied request, so a downstream log line can
/// name the gateway route that produced it without correlating by path pattern.
/// </summary>
public sealed class GatewayTraceHeaderSettings
{
    /// <summary>Default name of the header carrying the matched route id.</summary>
    public const string DefaultRouteHeaderName = "X-MMCA-Route";

    /// <summary>Default name of the header carrying the target cluster id.</summary>
    public const string DefaultClusterHeaderName = "X-MMCA-Cluster";

    /// <summary>Whether the headers are stamped at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Header carrying the matched route id.</summary>
    [Required]
    public string RouteHeaderName { get; init; } = DefaultRouteHeaderName;

    /// <summary>Header carrying the target cluster id.</summary>
    [Required]
    public string ClusterHeaderName { get; init; } = DefaultClusterHeaderName;
}

/// <summary>What a named per-route rate-limiter policy counts requests against.</summary>
public enum GatewayRoutePolicyPartition
{
    /// <summary>One window per client IP. The edge default: at the edge there is no principal yet.</summary>
    ClientIp = 0,

    /// <summary>One window for the whole replica, whoever the caller is.</summary>
    Global = 1,
}

/// <summary>
/// A named fixed-window policy a route can reference through YARP's own <c>RateLimiterPolicy</c>
/// route property, e.g. <c>"RateLimiterPolicy": "auth-tight"</c>.
/// </summary>
public sealed class GatewayRoutePolicySettings
{
    /// <summary>What the window counts against.</summary>
    public GatewayRoutePolicyPartition Partition { get; init; } = GatewayRoutePolicyPartition.ClientIp;

    /// <summary>Requests allowed per window.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; init; } = 30;

    /// <summary>Window length in seconds.</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; init; } = 60;

    /// <summary>
    /// Requests parked when the window is exhausted. Zero (reject immediately) is the default: a
    /// queue at the edge converts a throttle into latency the caller cannot see the cause of.
    /// </summary>
    [Range(0, 10_000)]
    public int QueueLimit { get; init; }

    /// <summary>Single partition key used by <see cref="GatewayRoutePolicyPartition.Global"/>.</summary>
    internal const string GlobalPartitionKey = "__global";

    /// <summary>
    /// The partition key this request counts against under this policy, or <see langword="null"/>
    /// when the client IP is unresolvable and the policy is per-IP. Null means "no limiter": failing
    /// open beats collapsing every unattributable request into one shared bucket, which throttles an
    /// in-process TestServer to a standstill.
    /// </summary>
    /// <param name="remoteIpAddress">The resolved client address, if any.</param>
    /// <returns>The partition key, or <see langword="null"/> to apply no limiter at all.</returns>
    internal string? PartitionKey(IPAddress? remoteIpAddress) =>
        Partition == GatewayRoutePolicyPartition.Global
            ? GlobalPartitionKey
            : remoteIpAddress?.ToString();
}
