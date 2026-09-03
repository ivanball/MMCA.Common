using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

/// <summary>
/// OpenTelemetry instruments for the outbox pipeline, emitted by <see cref="OutboxProcessor"/>.
/// A host exports them by registering the <see cref="MeterName"/> meter: the Aspire service
/// defaults (<c>ConfigureOpenTelemetry</c>) already do. The meter name is duplicated as a literal
/// in MMCA.Common.Aspire because that package has no reference to Infrastructure.
/// <para>
/// One meter serves every outbox instrument: dead letters, successful dispatches, dispatch lag,
/// and observed backlog depth. Never create a second <see cref="Meter"/> with this name.
/// </para>
/// </summary>
internal static class OutboxMetrics
{
    /// <summary>OpenTelemetry meter name for outbox metrics.</summary>
    internal const string MeterName = "MMCA.Common.Outbox";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Backing store for <see cref="PendingDepthGauge"/>: the backlog this process observed at the
    /// start of its most recent processing cycle, summed across every outbox source it drains.
    /// </summary>
    private static long _pendingDepth;

    /// <summary>
    /// Backing store for <see cref="OldestPendingAgeGauge"/>: the age in seconds of the oldest
    /// pending row each source showed on its most recent cycle, keyed by the source name used as
    /// the gauge's <c>data_source</c> tag.
    /// </summary>
    private static readonly ConcurrentDictionary<string, double> OldestPendingAgeSeconds =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Messages dead-lettered, tagged by <c>event_type</c> and by <c>reason</c>
    /// (<c>type_unresolvable</c> or <c>retries_exhausted</c>).
    /// </summary>
    internal static readonly Counter<long> DeadLetterCounter = Meter.CreateCounter<long>(
        "outbox.dead_letter.count",
        "messages",
        "Number of outbox messages dead-lettered due to unresolvable event types");

    /// <summary>Messages dispatched successfully and stamped processed, tagged by <c>event_type</c>.</summary>
    internal static readonly Counter<long> ProcessedCounter = Meter.CreateCounter<long>(
        "outbox.processed.count",
        unit: "messages",
        description: "Number of outbox messages dispatched successfully, tagged by event type.");

    /// <summary>
    /// End-to-end delivery lag in SECONDS: the interval between an event being written to the
    /// outbox (<c>OccurredOn</c>) and being stamped <c>ProcessedOn</c>, tagged by <c>event_type</c>.
    /// This is the number that answers "how far behind is eventual consistency right now".
    /// </summary>
    internal static readonly Histogram<double> DispatchLagHistogram = Meter.CreateHistogram<double>(
        "outbox.dispatch.lag",
        unit: "s",
        description: "Seconds between an outbox message being written and being dispatched, tagged by event type.");

    /// <summary>
    /// Backlog depth observed at the start of the most recent processing cycle, summed across
    /// every outbox source this host drains.
    /// </summary>
    /// <remarks>
    /// This gauge assumes a SINGLE hosted processor instance per process (the deployment
    /// convention for the outbox processor). It reports what THIS instance last observed, not a
    /// cluster-wide depth: with several replicas running, each publishes its own view and the
    /// values must be read per instance, never summed into a fleet total. The count uses the same
    /// predicate as the poll (unprocessed, retries not exhausted, not under an unexpired lease),
    /// so rows another replica currently holds are excluded, and a source whose database is
    /// unreachable contributes zero for that cycle rather than holding a stale value.
    /// </remarks>
    internal static readonly ObservableGauge<long> PendingDepthGauge = Meter.CreateObservableGauge(
        "outbox.pending.depth",
        () => Interlocked.Read(ref _pendingDepth),
        unit: "messages",
        description: "Outbox rows awaiting dispatch, as observed by this processor instance on its last cycle.");

    /// <summary>
    /// Age in SECONDS of the oldest row still awaiting dispatch, per <c>data_source</c>, as observed
    /// at the start of that source's most recent cycle. Where <c>outbox.dispatch.lag</c> reports how
    /// late the messages that DID get delivered were, this one reports how late the backlog already
    /// is while it is still stuck, which is the number an alert on a wedged outbox fires on.
    /// </summary>
    /// <remarks>
    /// Costs nothing extra to compute: the poll already fetches pending rows ordered by
    /// <c>OccurredOn</c>, so the first row of that fetch IS the minimum, and no separate
    /// <c>MIN()</c> query is ever issued (the same discipline the depth gauge applies to its
    /// <c>COUNT</c>). Two consequences follow from reusing the poll's predicate: rows held under
    /// another replica's unexpired lease are excluded, and rows that have exhausted their retries
    /// (dead letters) are excluded, so this measures deliverable backlog rather than table age.
    /// A source with nothing pending reports <c>0</c> rather than dropping out of the series, so a
    /// drained outbox is visibly drained instead of indistinguishable from a stopped host; a source
    /// whose database was unreachable keeps its previous value until its next successful cycle.
    /// </remarks>
    internal static readonly ObservableGauge<double> OldestPendingAgeGauge = Meter.CreateObservableGauge(
        "outbox.oldest_pending.age",
        ObserveOldestPendingAge,
        unit: "s",
        description: "Age in seconds of the oldest outbox row awaiting dispatch, tagged by data source.");

    /// <summary>
    /// Publishes the backlog depth observed by the processor for the cycle that just ran.
    /// </summary>
    /// <param name="depth">Pending rows summed across every source drained this cycle.</param>
    internal static void SetPendingDepth(long depth) => Interlocked.Exchange(ref _pendingDepth, depth);

    /// <summary>
    /// Publishes the age of the oldest pending row for one source, for the cycle that just ran.
    /// </summary>
    /// <param name="dataSourceName">The source name, emitted as the <c>data_source</c> tag.</param>
    /// <param name="ageSeconds">Age of the oldest pending row in seconds; <c>0</c> when none is pending.</param>
    internal static void SetOldestPendingAge(string dataSourceName, double ageSeconds) =>
        OldestPendingAgeSeconds[dataSourceName] = ageSeconds;

    private static IEnumerable<Measurement<double>> ObserveOldestPendingAge()
    {
        foreach (KeyValuePair<string, double> entry in OldestPendingAgeSeconds)
        {
            yield return new Measurement<double>(
                entry.Value,
                new KeyValuePair<string, object?>("data_source", entry.Key));
        }
    }
}
