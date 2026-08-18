using System.Diagnostics.Metrics;

namespace MMCA.Common.Infrastructure.Messaging;

/// <summary>
/// OpenTelemetry instruments for the broker transport: consumer faults observed by
/// <c>FaultIntegrationEventConsumer&lt;TEvent&gt;</c> and circuit-breaker openings observed by
/// the outbox publish path. A host exports them by registering the <see cref="MeterName"/> meter:
/// the Aspire service defaults (<c>ConfigureOpenTelemetry</c>) already do. The meter name is
/// duplicated as a literal in MMCA.Common.Aspire because that package has no reference to
/// Infrastructure.
/// <para>
/// One meter serves every broker instrument. Never create a second <see cref="Meter"/> with this
/// name: a duplicate instance publishes a parallel set of instruments under the same meter name,
/// and a listener enabling one of them silently misses the measurements recorded on the other.
/// </para>
/// </summary>
internal static class BrokerMetrics
{
    /// <summary>OpenTelemetry meter name for broker transport metrics.</summary>
    internal const string MeterName = "MMCA.Common.Broker";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Messages that exhausted their retry policy and were published as a MassTransit
    /// <c>Fault&lt;TEvent&gt;</c>, tagged by <c>event_type</c>. A non-zero rate here means events
    /// are being lost to the error queue, so it is the natural alert target for consumer health.
    /// </summary>
    internal static readonly Counter<long> FaultCounter = Meter.CreateCounter<long>(
        "broker.fault.count",
        unit: "messages",
        description: "Number of integration events that exhausted retries and faulted, tagged by event type.");

    /// <summary>
    /// Times the outbox broker-publish circuit breaker rejected a publish because the circuit was
    /// open, tagged by <c>event_type</c>. Distinct from a publish failure: these attempts never
    /// reached the broker at all, which is the whole point of the breaker (fail fast rather than
    /// stack publish timeouts against a dead broker). The affected rows stay leased and are
    /// retried on a later cycle.
    /// </summary>
    internal static readonly Counter<long> CircuitOpenCounter = Meter.CreateCounter<long>(
        "broker.circuit.open.count",
        unit: "messages",
        description: "Number of outbox publishes short-circuited by the open broker circuit breaker, tagged by event type.");
}
