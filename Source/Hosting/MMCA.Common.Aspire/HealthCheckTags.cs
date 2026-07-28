namespace MMCA.Common.Aspire;

/// <summary>
/// Tag names the standard health endpoints from <c>MapDefaultEndpoints()</c> filter on.
/// </summary>
public static class HealthCheckTags
{
    /// <summary>
    /// Liveness-only: the check runs on <c>/alive</c> and is EXCLUDED from readiness. Reserved for
    /// self checks, so an external dependency outage never restarts the container.
    /// </summary>
    public const string Live = "live";

    /// <summary>
    /// Readiness gate: the check runs on <c>/health/ready</c> and holds traffic back until it
    /// passes. Used by the warm-up gate.
    /// </summary>
    public const string Ready = "ready";

    /// <summary>
    /// A dependency the application degrades gracefully without: it is reported on <c>/health</c>
    /// but EXCLUDED from <c>/health/ready</c>.
    /// <para>
    /// The distinction is load-bearing. A distributed cache sitting behind an in-memory fallback, or
    /// a broker behind a retrying outbox, can fail without the app losing the ability to serve.
    /// Gating readiness on it converts a partial degradation into a TOTAL outage: every replica goes
    /// unready simultaneously and the platform stops routing traffic the app could still handle.
    /// Tag such checks <see cref="Optional"/>; leave a check untagged only when the app genuinely
    /// cannot serve correct responses without it (its own database being the standard example).
    /// </para>
    /// </summary>
    public const string Optional = "optional";
}
