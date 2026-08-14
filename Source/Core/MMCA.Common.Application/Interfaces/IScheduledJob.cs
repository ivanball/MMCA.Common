namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// A unit of recurring work driven by a cron schedule. Implementations are registered with
/// <c>AddScheduledJob&lt;TJob&gt;()</c> and executed by the framework's scheduler once
/// <c>AddScheduledJobs(configuration)</c> has been called and <c>Scheduler:Enabled</c> is true.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Jobs are resolved <b>scoped</b>, in a fresh DI scope created for each
/// execution, exactly like a request. A job may therefore take scoped dependencies (a unit of
/// work, a repository, a command handler) and must not hold state between executions: the
/// instance that ran the previous occurrence is already disposed.
/// </para>
/// <para>
/// <b>Single runner across replicas.</b> A scaled-out service runs one scheduler per replica, but
/// an occurrence executes exactly once: the persistent job store hands out a claim lease per row
/// (the outbox processor's claim idiom), so the replica that wins the claim is the only one that
/// runs the job. A replica that dies mid-execution releases its claim implicitly when the lease
/// expires, and the occurrence is picked up again.
/// </para>
/// <para>
/// <b>Missed occurrences do not pile up.</b> After an outage (or any window where no replica was
/// running), a job whose schedule elapsed several times runs <b>once</b> and its next run is then
/// computed from the current instant, not from the backlog. There is no catch-up storm: a job that
/// missed forty hourly occurrences overnight runs once at startup and returns to its hourly
/// cadence. Work that must not be skipped therefore has to be idempotent and range-driven
/// (process everything since the last successful run) rather than relying on one run per tick.
/// </para>
/// <para>
/// <b>Failures are recorded, not fatal.</b> An exception thrown by <see cref="ExecuteAsync"/> is
/// caught, logged and stamped on the job row as a failed outcome; the schedule still advances and
/// the scheduler loop survives. A job is never retried within its own occurrence.
/// </para>
/// </remarks>
public interface IScheduledJob
{
    /// <summary>
    /// Gets the stable identity of this job, unique within the host. It is the primary key of the
    /// persisted job row and the value the scheduler resolves back to this implementation, so it
    /// must not change once deployed (renaming it strands the old row and starts a new schedule)
    /// and two registered jobs must never share it.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the default schedule as a <b>five-field</b> cron expression
    /// (<c>minute hour day-of-month month day-of-week</c>), parsed by Cronos.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Examples: <c>*/5 * * * *</c> (every five minutes), <c>0 * * * *</c> (hourly, on the hour),
    /// <c>0 3 * * *</c> (daily at 03:00), <c>0 2 * * 1</c> (Mondays at 02:00). Cronos also accepts
    /// ranges (<c>1-5</c>), lists (<c>1,15</c>), steps (<c>*/10</c>), and <c>L</c> / <c>W</c> /
    /// <c>#</c> in the day fields.
    /// </para>
    /// <para>
    /// <b>All times are UTC.</b> Occurrences are computed against the UTC clock, never a local or
    /// configured time zone, so a schedule never shifts, doubles or vanishes across a daylight
    /// saving transition. A job that must fire at a wall-clock local hour has to encode the offset
    /// itself and accept the seasonal one-hour drift.
    /// </para>
    /// <para>
    /// This value is the <b>default</b>. A host overrides it per job without touching code through
    /// <c>Scheduler:Jobs:{Name}:Cron</c> in configuration, which wins whenever it is present.
    /// </para>
    /// </remarks>
    string CronExpression { get; }

    /// <summary>
    /// Runs one occurrence of the job.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancelled on host shutdown. Long-running jobs should honor it: work that ignores it delays
    /// shutdown and can outlive its claim lease.
    /// </param>
    /// <returns>A task that completes when the occurrence has finished.</returns>
    Task ExecuteAsync(CancellationToken cancellationToken);
}
