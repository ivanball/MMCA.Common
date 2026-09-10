using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// OpenTelemetry instruments for the internal-command job queue, emitted by
/// <see cref="InternalCommandProcessor"/>. A host exports them by registering the
/// <see cref="MeterName"/> meter: the Aspire service defaults (<c>ConfigureOpenTelemetry</c>) already
/// do. The meter name is duplicated as a literal in MMCA.Common.Aspire because that package has no
/// reference to Infrastructure.
/// <para>
/// One meter serves every instrument here: completions, dead letters, execution latency, and
/// observed backlog depth. Never create a second <see cref="Meter"/> with this name.
/// </para>
/// </summary>
internal static class InternalCommandMetrics
{
    /// <summary>OpenTelemetry meter name for internal-command metrics.</summary>
    internal const string MeterName = "MMCA.Common.InternalCommands";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Backing store for <see cref="PendingDepthGauge"/>: the backlog this process observed at the
    /// start of its most recent cycle, summed across every source it drains.
    /// </summary>
    private static long _pendingDepth;

    /// <summary>
    /// Backing store for <see cref="OldestDueAgeGauge"/>: how overdue the oldest due row was on each
    /// source's most recent cycle, keyed by the source name used as the <c>data_source</c> tag.
    /// </summary>
    private static readonly ConcurrentDictionary<string, double> OldestDueAgeSeconds =
        new(StringComparer.Ordinal);

    /// <summary>Commands that completed successfully, tagged by <c>command_type</c>.</summary>
    internal static readonly Counter<long> ProcessedCounter = Meter.CreateCounter<long>(
        "internal_commands.processed.count",
        unit: "commands",
        description: "Internal commands executed successfully, tagged by command type.");

    /// <summary>
    /// Commands that failed one attempt, tagged by <c>command_type</c> and by <c>reason</c>
    /// (<c>result_failure</c> or <c>exception</c>). A dead letter is counted here too, on the attempt
    /// that exhausted the budget.
    /// </summary>
    internal static readonly Counter<long> FailedCounter = Meter.CreateCounter<long>(
        "internal_commands.failed.count",
        unit: "commands",
        description: "Internal command attempts that failed, tagged by command type and reason.");

    /// <summary>
    /// Commands abandoned, tagged by <c>command_type</c> and by <c>reason</c>
    /// (<c>attempts_exhausted</c>, <c>type_unresolvable</c> or <c>handler_missing</c>).
    /// </summary>
    internal static readonly Counter<long> DeadLetterCounter = Meter.CreateCounter<long>(
        "internal_commands.dead_letter.count",
        unit: "commands",
        description: "Internal commands dead-lettered, tagged by command type and reason.");

    /// <summary>
    /// How long, in SECONDS, one execution took, tagged by <c>command_type</c>. This measures the
    /// whole decorated pipeline, which is what an operator sizing the claim lease needs.
    /// </summary>
    internal static readonly Histogram<double> DurationHistogram = Meter.CreateHistogram<double>(
        "internal_commands.execution.duration",
        unit: "s",
        description: "Seconds one internal command execution took, tagged by command type.");

    /// <summary>
    /// How late, in SECONDS, an execution started relative to the instant it was scheduled for,
    /// tagged by <c>command_type</c>. This is the number that answers "is the queue keeping up".
    /// </summary>
    internal static readonly Histogram<double> LagHistogram = Meter.CreateHistogram<double>(
        "internal_commands.execution.lag",
        unit: "s",
        description: "Seconds between an internal command becoming due and starting, tagged by command type.");

    /// <summary>
    /// Backlog depth observed at the start of the most recent cycle, summed across every source this
    /// host drains.
    /// </summary>
    /// <remarks>
    /// Reports what THIS instance last observed, not a cluster-wide depth: with several replicas
    /// running, each publishes its own view and the values must be read per instance, never summed
    /// into a fleet total. The count uses the same predicate as the poll (not completed, not
    /// dead-lettered, attempts not exhausted, not under an unexpired lease), so rows another replica
    /// currently holds are excluded, and a source whose database is unreachable contributes zero for
    /// that cycle rather than holding a stale value.
    /// </remarks>
    internal static readonly ObservableGauge<long> PendingDepthGauge = Meter.CreateObservableGauge(
        "internal_commands.pending.depth",
        () => Interlocked.Read(ref _pendingDepth),
        unit: "commands",
        description: "Internal-command rows awaiting execution, as observed by this processor instance on its last cycle.");

    /// <summary>
    /// Age in SECONDS of the oldest row that is already due and still unexecuted, per
    /// <c>data_source</c>. Where the lag histogram reports how late the commands that DID run were,
    /// this reports how late the backlog already is while it is still stuck, which is what an alert
    /// on a wedged queue fires on.
    /// </summary>
    internal static readonly ObservableGauge<double> OldestDueAgeGauge = Meter.CreateObservableGauge(
        "internal_commands.oldest_due.age",
        ObserveOldestDueAge,
        unit: "s",
        description: "Age in seconds of the oldest due internal-command row, tagged by data source.");

    /// <summary>Publishes the backlog depth observed for the cycle that just ran.</summary>
    /// <param name="depth">Pending rows summed across every source drained this cycle.</param>
    internal static void SetPendingDepth(long depth) => Interlocked.Exchange(ref _pendingDepth, depth);

    /// <summary>Publishes how overdue one source's oldest due row was, for the cycle that just ran.</summary>
    /// <param name="dataSourceName">The source name, emitted as the <c>data_source</c> tag.</param>
    /// <param name="ageSeconds">Age of the oldest due row in seconds; <c>0</c> when none is due.</param>
    internal static void SetOldestDueAge(string dataSourceName, double ageSeconds) =>
        OldestDueAgeSeconds[dataSourceName] = ageSeconds;

    private static IEnumerable<Measurement<double>> ObserveOldestDueAge()
    {
        foreach (KeyValuePair<string, double> entry in OldestDueAgeSeconds)
        {
            yield return new Measurement<double>(
                entry.Value,
                new KeyValuePair<string, object?>("data_source", entry.Key));
        }
    }
}
