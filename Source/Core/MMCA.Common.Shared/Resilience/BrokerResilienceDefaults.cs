namespace MMCA.Common.Shared.Resilience;

/// <summary>
/// Single source of truth for the circuit-breaker values guarding the outbox's broker-publish
/// path (<c>OutboxProcessor</c> in MMCA.Common.Infrastructure). Lives in Shared alongside
/// <see cref="HttpResilienceDefaults"/> so the numbers are reviewable in one place rather than
/// buried as literals inside a background service.
/// <para>
/// Why the outbox needs a breaker at all: when the broker is down, every publish in a 50-row
/// batch waits out its own transport timeout before failing, so one cycle can spend minutes doing
/// nothing but timing out, and the retry loop then queues the same wait again on the next cycle.
/// The breaker turns the second and later attempts into an immediate rejection. Nothing is lost by
/// failing fast: an outbox row that is not published stays unprocessed, keeps its lease-based
/// backoff and is retried on a later cycle exactly as a normal publish failure would be. The
/// breaker only changes how long the processor spends discovering that the broker is still down.
/// </para>
/// <para>
/// Deliberately NOT paired with a retry strategy: the outbox already owns retry (RetryCount,
/// exponential backoff with jitter, MaxRetries then dead-letter). A Polly retry inside a publish
/// would multiply against that budget and make the row's effective attempt count an accident of
/// two independent policies.
/// </para>
/// </summary>
public static class BrokerResilienceDefaults
{
    /// <summary>
    /// Fraction of failed publishes within <see cref="SamplingDuration"/> that opens the circuit.
    /// Half, rather than a lower bar: a partially degraded broker still delivering half its
    /// messages is worth continuing to drain, and only a clearly one-sided failure rate is
    /// evidence that further attempts this cycle are wasted.
    /// </summary>
    public static double FailureRatio => 0.5;

    /// <summary>
    /// Minimum publish attempts within <see cref="SamplingDuration"/> before the failure ratio is
    /// evaluated at all. Ten keeps a quiet host, where two attempts an hour is normal traffic,
    /// from opening the circuit on a single unlucky pair; the breaker should react to a broker
    /// outage, not to sparse traffic.
    /// </summary>
    public static int MinimumThroughput => 10;

    /// <summary>
    /// Rolling window over which the failure ratio is measured. Thirty seconds is a few outbox
    /// cycles at the default polling interval: long enough that a batch's worth of attempts lands
    /// inside one window, short enough that a resolved outage ages out of the statistics quickly.
    /// </summary>
    public static TimeSpan SamplingDuration => TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the circuit stays open before a single trial publish is allowed through. Fifteen
    /// seconds is deliberately short: a rejected publish costs one outbox row a retry increment,
    /// not a customer-facing error, so recovery latency matters more here than protecting the
    /// broker from one probe.
    /// </summary>
    public static TimeSpan BreakDuration => TimeSpan.FromSeconds(15);
}
