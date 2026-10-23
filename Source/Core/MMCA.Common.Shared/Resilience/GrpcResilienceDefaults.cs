namespace MMCA.Common.Shared.Resilience;

/// <summary>
/// Resilience values for the typed gRPC clients registered by <c>MMCA.Common.Grpc</c>. The timeouts
/// are the east-west path's own, sized from measured gRPC latency rather than inherited from
/// <see cref="HttpResilienceDefaults"/>: an in-cluster gRPC call answers in well under a second
/// (production p99 peaks below 800ms), so the outbound-HTTP 30s/90s budget would hold a caller on a
/// hung peer for a minute and a half. The attempt timeout is about ten times the slowest measured
/// p99 and the total covers two attempts plus the retry backoff. The retry budget and the breaker's
/// sampling window stay shared with the HTTP path, and the circuit-breaker shape is stated
/// explicitly here rather than left at the library defaults: east-west gRPC calls address a peer
/// directly and bypass the Gateway's active health checks, so the breaker is the only thing that
/// notices a peer going bad. A gRPC-status-aware retry predicate is deliberately deferred; retries
/// stay at the HTTP level, where the standard handler already classifies transient faults.
/// </summary>
public static class GrpcResilienceDefaults
{
    /// <summary>Per-attempt timeout for a single call attempt: about ten times the slowest measured east-west gRPC p99 (784ms).</summary>
    public static TimeSpan AttemptTimeout => TimeSpan.FromSeconds(8);

    /// <summary>Total call timeout including all retries: two attempts plus the retry backoff.</summary>
    public static TimeSpan TotalRequestTimeout => TimeSpan.FromSeconds(20);

    /// <summary>Sampling window for the circuit breaker's failure-ratio calculation (same value as the outbound-HTTP path, and at least twice <see cref="AttemptTimeout"/> as the resilience library requires).</summary>
    public static TimeSpan SamplingDuration => HttpResilienceDefaults.CircuitBreakerSamplingDuration;

    /// <summary>Retries beyond the initial attempt, kept at the outbound-HTTP budget of one so no hop multiplies a brownout into a request storm.</summary>
    public static int MaxRetryAttempts => HttpResilienceDefaults.MaxRetryAttempts;

    /// <summary>Half the calls in the sampling window must fail before the breaker opens: an in-cluster peer is healthy or hard-down, so a tighter ratio would trip on ordinary replica-rollover blips.</summary>
    public static double FailureRatio => 0.5;

    /// <summary>Minimum calls in the sampling window before the ratio is evaluated: ten keeps a single failed call against a low-traffic service from opening the breaker.</summary>
    public static int MinimumThroughput => 10;

    /// <summary>How long the breaker stays open before it probes again: ten seconds is about one container-replica restart, short enough that recovery is not gated on the breaker.</summary>
    public static TimeSpan BreakDuration => TimeSpan.FromSeconds(10);
}
