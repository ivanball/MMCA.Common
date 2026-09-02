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
/// <para>
/// <b>Synthetic traffic.</b> A scheduled capacity proof runs its whole load from ONE runner IP, so
/// the per-IP window reads it as a flood and answers almost entirely with <c>429</c>. Setting
/// <see cref="SyntheticTrafficSecret"/> lets such a run present
/// <see cref="SyntheticTrafficHeaderName"/> and take the same no-limiter partition a bypassed path
/// takes. It is off unless the secret is configured.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// "GatewayRateLimiting": {
///   "PermitLimit": 240,
///   "WindowSeconds": 60,
///   "GlobalConcurrencyLimit": 300,
///   "BypassPathPrefixes": [ "/metrics" ],
///   "SyntheticTrafficHeaderName": "X-Synthetic-Traffic-Key"
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

    /// <summary>
    /// Name of the request header a synthetic-traffic run presents to claim the bypass. Only the
    /// NAME lives here: the header is worthless without <see cref="SyntheticTrafficSecret"/>, which
    /// is why this one is safe in <c>appsettings.json</c> and the secret is not. Renaming it is a
    /// coordination change, not a security control.
    /// </summary>
    [Required]
    public string SyntheticTrafficHeaderName { get; init; } = "X-Synthetic-Traffic-Key";

    /// <summary>
    /// Shared secret that <see cref="SyntheticTrafficHeaderName"/> must carry, compared in constant
    /// time. Null or empty (the default) disables the bypass entirely, so no header value can claim
    /// it. When set it must be at least 32 characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> A load or capacity proof drives its whole run from ONE runner IP, which
    /// the per-IP fixed window cannot tell from an unauthenticated flood: the run measures the
    /// limiter instead of the system. A run holding this secret takes the no-limiter partition on
    /// BOTH chained limiters and measures the thing it was pointed at.
    /// </para>
    /// <para>
    /// <b>Where it belongs.</b> Production must supply it from a secret store or the environment
    /// (<c>GatewayRateLimiting__SyntheticTrafficSecret</c>), NEVER from a checked-in
    /// <c>appsettings</c> file: a value in source control is a published key to the edge limiter.
    /// The 32-character minimum is validated at registration, so a short value fails startup rather
    /// than shipping a guessable bypass. <c>StringLength</c> treats null as valid, which is exactly
    /// the intended "off" state.
    /// </para>
    /// </remarks>
    [StringLength(int.MaxValue, MinimumLength = 32)]
    public string? SyntheticTrafficSecret { get; init; }
}
