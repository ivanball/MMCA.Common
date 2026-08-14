namespace MMCA.Common.Infrastructure.Scheduling;

/// <summary>
/// The persisted schedule and last-run record for one registered
/// <see cref="Application.Interfaces.IScheduledJob"/>. One row per job name, upserted by
/// <see cref="ScheduledJobRunner"/> on every cycle.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> an <c>IAuditableEntity</c>, exactly like <c>OutboxMessage</c>: this is
/// framework bookkeeping, not domain state. It carries no soft-delete flag (so no global query
/// filter applies to it), no audit stamps, and no concurrency token. Its concurrency control is
/// the explicit claim lease below, which is what makes a scaled-out host safe.
/// </para>
/// <para>
/// The table is host-scoped and lives in the <b>Default</b> data source only, unlike the outbox,
/// which exists once per physical source. Jobs belong to a host, not to a database.
/// </para>
/// </remarks>
public sealed class ScheduledJobEntry
{
    /// <summary>
    /// Gets the job's stable name, matching <see cref="Application.Interfaces.IScheduledJob.Name"/>.
    /// This is the primary key: a job has exactly one schedule row per host.
    /// </summary>
    public required string JobName { get; init; }

    /// <summary>
    /// Gets or sets the five-field UTC cron expression currently in force, which is either the
    /// job's code default or the <c>Scheduler:Jobs:{Name}:Cron</c> configuration override. The
    /// runner compares this against the resolved expression on every cycle and recomputes
    /// <see cref="NextRunOn"/> whenever it has changed.
    /// </summary>
    public required string CronExpression { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant at which this job next becomes due. A row whose expression
    /// cannot be parsed is parked at <see cref="DateTime.MaxValue"/> so it is never claimed.
    /// </summary>
    public DateTime NextRunOn { get; set; }

    /// <summary>Gets or sets the UTC instant the most recent execution started. Null until the job has run once.</summary>
    public DateTime? LastRunOn { get; set; }

    /// <summary>
    /// Gets or sets the result of the most recent execution: <c>Succeeded</c>, <c>Failed</c>, or
    /// <c>Skipped</c> (the schedule could not be honored, e.g. an unparsable cron expression or a
    /// row whose job is no longer registered in this host).
    /// </summary>
    public string? LastOutcome { get; set; }

    /// <summary>
    /// Gets or sets the failure message from the most recent execution, truncated to the column
    /// width. Cleared on a successful run so the column always describes the last outcome rather
    /// than the last failure at any point in history.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets how long, in milliseconds, the most recent execution took.</summary>
    public long? LastDurationMs { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant until which this row is claimed by one scheduler replica.
    /// Other replicas skip rows with an unexpired lease, so an occurrence runs exactly once even
    /// with several replicas polling. Null or expired means claimable.
    /// </summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>
    /// Gets or sets the claim token written together with <see cref="LockedUntil"/>. The claiming
    /// replica stamps the outcome only on a row still carrying its own token, so a replica whose
    /// lease expired mid-execution cannot overwrite the record of the replica that took over.
    /// </summary>
    public Guid? LockToken { get; set; }
}
