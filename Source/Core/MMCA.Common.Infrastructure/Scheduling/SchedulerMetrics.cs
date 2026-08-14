using System.Diagnostics.Metrics;

namespace MMCA.Common.Infrastructure.Scheduling;

/// <summary>
/// OpenTelemetry instruments for the recurring job scheduler, emitted by
/// <see cref="ScheduledJobRunner"/>. A host exports them by registering the
/// <see cref="MeterName"/> meter: the Aspire service defaults (<c>ConfigureOpenTelemetry</c>)
/// already do. The meter name is duplicated as a literal in MMCA.Common.Aspire because that
/// package has no reference to Infrastructure.
/// <para>
/// One meter serves every scheduler instrument: run outcomes, execution duration, and schedule
/// lag. Never create a second <see cref="Meter"/> with this name.
/// </para>
/// </summary>
internal static class SchedulerMetrics
{
    /// <summary>OpenTelemetry meter name for scheduler metrics.</summary>
    internal const string MeterName = "MMCA.Common.Scheduler";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Job occurrences executed, tagged by <c>job</c> and by <c>outcome</c> (<c>Succeeded</c>,
    /// <c>Failed</c> or <c>Skipped</c>). The failure rate of a schedule is this counter split by
    /// outcome, which is the number an operator alerts on.
    /// </summary>
    internal static readonly Counter<long> RunCounter = Meter.CreateCounter<long>(
        "scheduler.job.runs",
        unit: "runs",
        description: "Number of scheduled job occurrences executed, tagged by job name and outcome.");

    /// <summary>
    /// Wall-clock execution time in SECONDS for one occurrence, tagged by <c>job</c>. Measured
    /// around the job's own <c>ExecuteAsync</c> only, so it excludes claim and bookkeeping cost.
    /// A job whose duration approaches <c>Scheduler:LeaseSeconds</c> is about to lose its claim
    /// mid-run, which is exactly what this histogram is for.
    /// </summary>
    internal static readonly Histogram<double> DurationHistogram = Meter.CreateHistogram<double>(
        "scheduler.job.duration",
        unit: "s",
        description: "Seconds spent executing one scheduled job occurrence, tagged by job name.");

    /// <summary>
    /// Schedule lag in SECONDS: the interval between the instant an occurrence became due
    /// (<c>NextRunOn</c>) and the instant it actually started, tagged by <c>job</c>. This is the
    /// number that answers "is the scheduler keeping up"; it is floored at zero, and a value near
    /// the polling interval is normal for a job whose occurrence landed just after a cycle.
    /// </summary>
    internal static readonly Histogram<double> LagHistogram = Meter.CreateHistogram<double>(
        "scheduler.job.lag",
        unit: "s",
        description: "Seconds between a scheduled job becoming due and its execution starting, tagged by job name.");
}
