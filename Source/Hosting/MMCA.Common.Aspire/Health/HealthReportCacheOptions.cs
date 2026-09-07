using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Aspire.Health;

/// <summary>
/// How long <c>/health</c> and <c>/health/ready</c> reuse one computed health report before running
/// the dependency probes again. Bound from the <c>HealthChecks</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// SEC-Common-71 and SEC-ADC-17. Both endpoints are anonymous and sit on the always-bypassed
/// rate-limit prefix, and both used to execute EVERY registered check per request: a
/// <c>SELECT 1</c> on a pooled SQL connection, a Redis PING, a broker check, and on a gateway one
/// outbound <c>/alive</c> call per fronted service. One unauthenticated client turned N requests
/// into N x (services + database + cache) backend operations, exhausted the SQL pool and took the
/// app down without an account. Caching the REPORT bounds that amplification at one probe round per
/// window regardless of inbound rate, which the bypass list alone cannot do.
/// </para>
/// <para>
/// <c>/alive</c> is deliberately NOT cached: it runs only the self check, costs nothing, and is what
/// the platform restarts a container on, so it must always answer for the current instant.
/// </para>
/// <para>
/// The default is short enough that a real outage is still noticed within one platform probe
/// interval, and long enough that a flood collapses onto a single probe round.
/// </para>
/// </remarks>
public sealed class HealthReportCacheOptions
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "HealthChecks";

    /// <summary>
    /// Seconds a computed report is reused. <c>0</c> disables caching and restores the
    /// probe-per-request behaviour.
    /// </summary>
    [Range(0, 300)]
    public int CacheSeconds { get; init; } = 5;
}
