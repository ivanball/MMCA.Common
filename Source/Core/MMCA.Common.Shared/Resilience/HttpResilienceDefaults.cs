namespace MMCA.Common.Shared.Resilience;

/// <summary>
/// Single source of truth for the outbound-HTTP resilience and socket-handler values used by
/// both <c>MMCA.Common.Aspire</c> (ConfigureHttpClientDefaults for every factory client) and
/// <c>MMCA.Common.Grpc</c> (typed gRPC clients). Lives in Shared because those two packages
/// may only depend on Shared per the layer rules; the previous hand-mirrored copies drifted
/// (10s/30s library defaults on the gRPC side vs the tuned 30s/90s on the HTTP side).
/// </summary>
public static class HttpResilienceDefaults
{
    /// <summary>Per-attempt timeout for a single HTTP request attempt.</summary>
    public static TimeSpan AttemptTimeout => TimeSpan.FromSeconds(30);

    /// <summary>Sampling window for the circuit breaker's failure-ratio calculation.</summary>
    public static TimeSpan CircuitBreakerSamplingDuration => TimeSpan.FromSeconds(60);

    /// <summary>Total request timeout including all retries.</summary>
    public static TimeSpan TotalRequestTimeout => TimeSpan.FromSeconds(90);

    /// <summary>
    /// Retries beyond the initial attempt. Kept at ONE deliberately: the UI service base
    /// classes own user-facing retries (their Polly policy makes up to 4 attempts), and each
    /// hop multiplying its own full retry budget turns a backend brownout into an up-to-16x
    /// request storm at exactly the wrong moment. One transient-fault retry per hop plus the
    /// UI-owned policy bounds the worst case while still absorbing connection blips.
    /// </summary>
    public static int MaxRetryAttempts => 1;

    /// <summary>
    /// Recycle pooled connections every 10 minutes so DNS changes (e.g. ACA replica rollover)
    /// are picked up without an app restart.
    /// </summary>
    public static TimeSpan PooledConnectionLifetime => TimeSpan.FromMinutes(10);

    /// <summary>Keep idle connections pooled for 5 minutes so low-traffic inter-service calls skip the handshake.</summary>
    public static TimeSpan PooledConnectionIdleTimeout => TimeSpan.FromMinutes(5);

    /// <summary>Socket-level keep-alive ping interval (does not count as user traffic to the ACA platform).</summary>
    public static TimeSpan KeepAlivePingDelay => TimeSpan.FromSeconds(60);

    /// <summary>Timeout waiting for a keep-alive ping acknowledgement.</summary>
    public static TimeSpan KeepAlivePingTimeout => TimeSpan.FromSeconds(30);
}
