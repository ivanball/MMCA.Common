namespace MMCA.Common.Shared.Resilience;

/// <summary>
/// Single source of truth for every resilience and socket-handler value the framework's HTTP-based
/// clients use: <c>MMCA.Common.Aspire</c> (ConfigureHttpClientDefaults for every factory client),
/// the UI service base classes, and <c>MMCA.Common.Grpc</c> (typed gRPC clients, which run over
/// <see cref="System.Net.Http.HttpClient"/> and its standard resilience handler). Every number lives
/// here, so one file answers "what timeout does this client get"; the <c>Grpc</c>-prefixed members are
/// the east-west gRPC path's own values. Lives in Shared because those packages may only depend on
/// Shared per the layer rules; the previous hand-mirrored copies drifted (10s/30s library defaults on
/// the gRPC side vs the tuned 30s/90s on the HTTP side).
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
    /// The retry never replays a mutation: the HTTP clients refuse it for POST and PATCH, and the gRPC
    /// clients (all POST) retry only a failure to reach the service.
    /// </summary>
    public static int MaxRetryAttempts => 1;

    /// <summary>
    /// The longest <c>Retry-After</c> a client will wait out before retrying a
    /// <c>429 Too Many Requests</c>: eight seconds, the longest step of the UI retry backoff. A 429 asking for more
    /// than this is not retried at all and surfaces to the caller at once, so a refused user action
    /// costs one request rather than a held connection waiting out the rest of a rate-limit window.
    /// </summary>
    public static TimeSpan MaxRetryAfterWait => TimeSpan.FromSeconds(8);

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

    // East-west gRPC (MMCA.Common.Grpc AddTypedGrpcClient). The timeouts are the gRPC path's own, sized
    // from measured latency rather than inherited from the outbound-HTTP values above: an in-cluster gRPC
    // call answers in well under a second (production p99 peaks below 800ms), so the 30s/90s budget would
    // hold a caller on a hung peer for a minute and a half. The retry budget (MaxRetryAttempts) and the
    // breaker's sampling window (CircuitBreakerSamplingDuration) are shared with the HTTP path. The
    // breaker shape is stated explicitly rather than left at the library defaults: an east-west call
    // addresses a peer directly and bypasses the Gateway's active health checks, so the breaker is the
    // only thing that notices a peer going bad. A consumer whose edge is legitimately slower overrides
    // these per client through AddTypedGrpcClient's configureResilience callback.

    /// <summary>
    /// Per-attempt timeout for a single gRPC call attempt: about ten times the slowest measured
    /// east-west gRPC p99 (784ms). <see cref="CircuitBreakerSamplingDuration"/> must stay at least twice
    /// this value, which the resilience library validates.
    /// </summary>
    public static TimeSpan GrpcAttemptTimeout => TimeSpan.FromSeconds(8);

    /// <summary>Total gRPC call timeout including all retries: two attempts plus the retry backoff.</summary>
    public static TimeSpan GrpcTotalRequestTimeout => TimeSpan.FromSeconds(20);

    /// <summary>
    /// Half the gRPC calls in the sampling window must fail before the breaker opens: an in-cluster peer
    /// is healthy or hard-down, so a tighter ratio would trip on ordinary replica-rollover blips.
    /// </summary>
    public static double GrpcCircuitBreakerFailureRatio => 0.5;

    /// <summary>
    /// Minimum gRPC calls in the sampling window before the ratio is evaluated: ten keeps a single failed
    /// call against a low-traffic service from opening the breaker.
    /// </summary>
    public static int GrpcCircuitBreakerMinimumThroughput => 10;

    /// <summary>
    /// How long the gRPC breaker stays open before it probes again: ten seconds is about one
    /// container-replica restart, short enough that recovery is not gated on the breaker.
    /// </summary>
    public static TimeSpan GrpcCircuitBreakerBreakDuration => TimeSpan.FromSeconds(10);
}
