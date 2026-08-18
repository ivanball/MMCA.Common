using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Aspire.Gateway;

/// <summary>
/// Configuration for <c>AddGatewayRateLimiting</c>, bound from the <c>GatewayRateLimiting</c>
/// section. Every property has a working default, so the section is optional in
/// <c>appsettings.json</c> and a host that omits it still gets the edge limiter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-replica, in memory.</b> Both limiters count in this process's memory, so the effective
/// allowance is the configured number MULTIPLIED BY the replica count: three replicas behind an
/// ingress admit roughly 3 x <see cref="PermitLimit"/> requests per window from one client IP. That
/// is the deliberate trade. An edge limiter's job is to keep one misbehaving caller from exhausting
/// a replica's sockets and threads, and it must answer in microseconds on every single request; a
/// shared counter would put a network round trip in front of the whole edge and would fail open (or
/// fail the edge) whenever the counter store blipped. Size the numbers per replica accordingly, and
/// reach for the distributed counter in <c>MMCA.Common.API</c>'s service-side
/// <c>RateLimitingSettings.Distributed</c> when a limit has to mean the same thing fleet-wide.
/// </para>
/// <para>
/// <b>Client IP.</b> Partitioning is by <c>Connection.RemoteIpAddress</c>, so
/// <c>UseForwardedHeaders</c> must run BEFORE <c>UseGatewayRateLimiting</c> or every request is
/// attributed to the ingress. An unresolvable IP fails open rather than collapsing into one shared
/// bucket that would throttle an in-process TestServer to a standstill.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// "GatewayRateLimiting": {
///   "PermitLimit": 240,
///   "WindowSeconds": 60,
///   "GlobalConcurrencyLimit": 300,
///   "BypassPathPrefixes": [ "/metrics" ]
/// }
/// </code>
/// </example>
public sealed class GatewayRateLimitingSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "GatewayRateLimiting";

    /// <summary>
    /// Requests per window per client IP, counted per replica. Applies to ANONYMOUS traffic too:
    /// unlike the service-side global limiter (which exempts anonymous callers because public reads
    /// are output-cached and Blazor Server traffic shares one host IP), the edge is exactly where an
    /// unauthenticated flood has to be stopped.
    /// </summary>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; init; } = 120;

    /// <summary>Length of the fixed window, in seconds, that <see cref="PermitLimit"/> is counted over.</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; init; } = 60;

    /// <summary>
    /// Maximum requests in flight through the gateway at once, across all clients, per replica.
    /// A concurrency cap (not a rate) because the failure this guards against is a slow downstream
    /// backing requests up until the edge runs out of threads and sockets: no rate limit prevents
    /// that, only a ceiling on simultaneous work does. Excess requests are rejected immediately with
    /// 429 rather than queued, so a saturated edge sheds load instead of growing latency.
    /// </summary>
    [Range(1, 1_000_000)]
    public int GlobalConcurrencyLimit { get; init; } = 200;

    /// <summary>
    /// Extra path prefixes exempt from both limiters, matched case-insensitively on path segments.
    /// Empty by default. The health endpoints (<c>/health</c>, <c>/alive</c>) and
    /// <c>/.well-known</c> are ALWAYS exempt regardless of this list: probes and JWKS discovery run
    /// at high frequency by design, and rate-limiting them turns a traffic spike into a failed
    /// liveness probe and a container restart.
    /// </summary>
    public IReadOnlyList<string> BypassPathPrefixes { get; init; } = [];
}
