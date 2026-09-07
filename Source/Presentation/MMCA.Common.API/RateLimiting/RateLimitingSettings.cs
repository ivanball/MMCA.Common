using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.API.RateLimiting;

/// <summary>
/// Configuration for <c>AddCommonRateLimiting</c>, bound from the <c>RateLimiting</c> section.
/// Every property has the same default the parameterized overload of
/// <c>AddCommonRateLimiting</c> has always used, so the section is optional in
/// <c>appsettings.json</c> and a host that omits it keeps the previous behaviour exactly.
/// </summary>
/// <example>
/// <code>
/// "RateLimiting": {
///   "GlobalPermitLimit": 600,
///   "Algorithm": "SlidingWindow",
///   "SegmentsPerWindow": 6,
///   "Distributed": true
/// }
/// </code>
/// </example>
public sealed class RateLimitingSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "RateLimiting";

    /// <summary>Requests per minute for the opt-in "FixedPolicy" limiter.</summary>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; init; } = 100;

    /// <summary>Queued requests allowed once "FixedPolicy" or "UserPolicy" are saturated.</summary>
    [Range(0, 10_000)]
    public int QueueLimit { get; init; } = 2;

    /// <summary>Requests per minute per user for the opt-in "UserPolicy" limiter.</summary>
    [Range(1, 1_000_000)]
    public int PerUserPermitLimit { get; init; } = 30;

    /// <summary>Requests per minute per authenticated user for the always-on global limiter.</summary>
    [Range(1, 1_000_000)]
    public int GlobalPermitLimit { get; init; } = 300;

    /// <summary>
    /// Requests per minute per client IP for the <c>auth-ip</c> policy that throttles anonymous
    /// authentication attempts.
    /// </summary>
    [Range(1, 1_000_000)]
    public int AuthIpPermitLimit { get; init; } = 30;

    /// <summary>
    /// The limiting algorithm for the in-memory partitions. Defaults to
    /// <see cref="RateLimitAlgorithm.FixedWindow"/>, which is what the framework has always used.
    /// </summary>
    public RateLimitAlgorithm Algorithm { get; init; } = RateLimitAlgorithm.FixedWindow;

    /// <summary>
    /// Number of segments the one-minute window is divided into when
    /// <see cref="Algorithm"/> is <see cref="RateLimitAlgorithm.SlidingWindow"/>. Ignored for
    /// <see cref="RateLimitAlgorithm.FixedWindow"/>. Higher values smooth the window further and
    /// cost one more counter per partition.
    /// </summary>
    [Range(1, 60)]
    public int SegmentsPerWindow { get; init; } = 4;

    /// <summary>
    /// Whether the global limiter and the "UserPolicy" limiter should count against a shared Redis
    /// counter instead of per-instance memory, so a limit means the same thing behind a load
    /// balancer as it does on one node. Requires an <c>IConnectionMultiplexer</c> in the container;
    /// when none is registered the limiters silently fall back to the in-memory (per-instance)
    /// behaviour rather than failing startup. The <c>auth-ip</c> policy stays in memory either way,
    /// because per-account login protection already backs it.
    /// </summary>
    public bool Distributed { get; init; }

    /// <summary>
    /// Path prefixes that host a real-time hub. Anonymous traffic to these paths is metered per
    /// client IP instead of taking the anonymous no-limiter partition (SEC-ADC-25).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The global limiter exempts anonymous callers on purpose: public reads are output-cached and
    /// server-rendered Blazor traffic shares one host IP. A hub is the exception. A gateway that
    /// bypasses <c>/hubs</c> at the edge (which ADR-024 requires, because the hub authenticates from
    /// a query-string token the edge cannot read) left <c>/hubs/*/negotiate</c> metered NOWHERE: an
    /// unauthenticated loop cost full middleware plus auth-reject CPU on every request with nothing
    /// counting it, unlike every other anonymous route, which the edge still limits.
    /// </para>
    /// <para>
    /// Authenticated hub traffic is unaffected: it already takes the per-user partition.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> HubPathPrefixes { get; init; } = ["/hubs"];

    /// <summary>
    /// Requests per minute per client IP for ANONYMOUS traffic to
    /// <see cref="HubPathPrefixes"/>. Generous, because one browser tab reconnecting behind a
    /// flaky network legitimately negotiates several times a minute.
    /// </summary>
    [Range(1, 1_000_000)]
    public int AnonymousHubPermitLimit { get; init; } = 60;
}
