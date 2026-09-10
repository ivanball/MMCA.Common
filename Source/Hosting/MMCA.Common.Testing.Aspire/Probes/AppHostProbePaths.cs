namespace MMCA.Common.Testing.Aspire.Probes;

/// <summary>
/// The request paths an AppHost-backed test probes a running service on.
/// <para>
/// These MIRROR the framework constants rather than referencing them: the paths are served by
/// <c>MMCA.Common.Aspire</c> (<c>HealthEndpointPaths</c>, mapped by <c>MapDefaultEndpoints()</c>) and
/// <c>MMCA.Common.API</c> (<c>JwksEndpointExtensions.DefaultJwksPath</c>), and pulling either of
/// those graphs into a test-infrastructure package to spell three strings is the wrong trade. A unit
/// test in <c>MMCA.Common.Testing.Aspire.Tests</c> cross-asserts each pair, so a rename cannot
/// silently orphan a probe: the same posture <c>MMCA.Common.Aspire.Hosting</c> takes for the gateway
/// configuration sections it mirrors.
/// </para>
/// </summary>
public static class AppHostProbePaths
{
    /// <summary>Full health report: every registered check must pass. Mirrors <c>HealthEndpointPaths.Health</c>.</summary>
    public const string Health = "/health";

    /// <summary>
    /// Liveness. This is the path a startup gate probes, never readiness: a readiness endpoint
    /// aggregates downstream and warm-up checks, so gating startup on it can deadlock the dependency
    /// graph (a gateway waits for a service to report ready, that service's readiness warms up
    /// through the gateway, neither side can finish first). Mirrors <c>HealthEndpointPaths.Alive</c>.
    /// </summary>
    public const string Alive = "/alive";

    /// <summary>
    /// Readiness: everything except live-only and optional checks. Asserted by a test that wants to
    /// know traffic may be routed, never used as a startup gate. Mirrors <c>HealthEndpointPaths.Ready</c>.
    /// </summary>
    public const string Ready = "/health/ready";

    /// <summary>The published key set. Mirrors <c>JwksEndpointExtensions.DefaultJwksPath</c>.</summary>
    public const string Jwks = "/.well-known/jwks.json";

    /// <summary>
    /// The Aspire endpoint name of a service's cleartext listener, which is the h2c one: an
    /// <c>https</c> endpoint negotiates the version through ALPN and needs no prior knowledge.
    /// </summary>
    public const string CleartextEndpointName = "http";
}
