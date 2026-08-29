namespace MMCA.Common.Shared.Resilience;

/// <summary>
/// Resilience values for the typed gRPC clients registered by <c>MMCA.Common.Grpc</c>. Timeouts
/// and the retry budget are re-exposed from <see cref="HttpResilienceDefaults"/> so the east-west
/// path can never drift from the outbound-HTTP path, while the circuit-breaker shape is stated
/// explicitly here rather than left at the library defaults: east-west gRPC calls address a peer
/// directly and bypass the Gateway's active health checks, so the breaker is the only thing that
/// notices a peer going bad. A gRPC-status-aware retry predicate is deliberately deferred; retries
/// stay at the HTTP level, where the standard handler already classifies transient faults.
/// </summary>
public static class GrpcResilienceDefaults
{
    /// <summary>Per-attempt timeout for a single call attempt (same value as the outbound-HTTP path).</summary>
    public static TimeSpan AttemptTimeout => HttpResilienceDefaults.AttemptTimeout;

    /// <summary>Total call timeout including all retries (same value as the outbound-HTTP path).</summary>
    public static TimeSpan TotalRequestTimeout => HttpResilienceDefaults.TotalRequestTimeout;

    /// <summary>Sampling window for the circuit breaker's failure-ratio calculation (same value as the outbound-HTTP path).</summary>
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
