namespace MMCA.Common.Aspire;

/// <summary>
/// The health-probe endpoint paths mapped by <c>MapDefaultEndpoints()</c>. Declared once so the
/// mapping, the probe telemetry filters and any host bypass list cannot drift apart: a new probe
/// route added to the mapping is automatically a probe route everywhere else.
/// </summary>
public static class HealthEndpointPaths
{
    /// <summary>Full health report: every registered check must pass.</summary>
    public const string Health = "/health";

    /// <summary>Liveness probe: only checks tagged <see cref="HealthCheckTags.Live"/> run.</summary>
    public const string Alive = "/alive";

    /// <summary>Readiness probe: everything except live-only and optional checks.</summary>
    public const string Ready = "/health/ready";

    private const string HealthPrefix = Health + "/";

    /// <summary>
    /// Whether <paramref name="path"/> addresses one of the health-probe endpoints: <c>/alive</c>,
    /// <c>/health</c>, or anything below <c>/health/</c> (which covers <c>/health/ready</c> and any
    /// sub-route a host adds later). The comparison is case-insensitive because ASP.NET Core
    /// routing is.
    /// </summary>
    /// <param name="path">An absolute request path without query string, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the path addresses a probe endpoint.</returns>
    public static bool IsProbePath(string? path)
        => !string.IsNullOrEmpty(path)
            && (string.Equals(path, Alive, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, Health, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(HealthPrefix, StringComparison.OrdinalIgnoreCase));
}
