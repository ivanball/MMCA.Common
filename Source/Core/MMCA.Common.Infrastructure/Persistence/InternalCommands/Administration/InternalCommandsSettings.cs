using System.ComponentModel.DataAnnotations;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;

/// <summary>
/// Configuration for the internal-command job queue, bound from the <c>InternalCommands</c> section.
/// Every property has a default, so the section is optional in <c>appsettings.json</c>.
/// <para>
/// The posture matches the outbox: the <c>InternalCommands</c> table is mapped into every relational
/// source whether or not the queue is running, so <see cref="Enabled"/> is never a migration, only a
/// decision about whether this host drains the queue.
/// </para>
/// </summary>
public sealed class InternalCommandsSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "InternalCommands";

    /// <summary>
    /// Gets a value indicating whether this host runs the processor and the retention sweep.
    /// Defaults to <see langword="true"/>, the outbox's posture: a host that schedules work must
    /// drain it, and a host that never schedules any pays one idle poll loop. Set it to
    /// <see langword="false"/> on a replica that must only write rows (a web front end in front of a
    /// dedicated worker, say); a startup notice states the choice once.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets the maximum number of due commands to claim per polling cycle.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// Gets the number of attempts a command gets before it is dead-lettered. A handler returning
    /// <c>Result.Failure</c> counts as an attempt exactly like a thrown exception: both mean the work
    /// did not happen.
    /// </summary>
    [Range(1, 20)]
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// Gets the fallback polling interval in seconds. The processor normally sleeps until the
    /// earliest scheduled row becomes due (the smart wait) or until a signal wakes it, so this only
    /// bounds how long a row scheduled inside a transaction waits: that row is enrolled rather than
    /// saved, so no signal is raised for it and the commit is discovered by the next poll. Raise it
    /// to cut idle polling, and accept that much latency on transaction-scheduled work.
    /// </summary>
    [Range(1, 3600)]
    public int PollingIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// Gets a grace period in seconds between a row becoming due and the processor attempting it.
    /// Defaults to <c>0</c>, unlike the outbox's 5: the outbox delay exists to bound a race with the
    /// in-process fast path that dispatches an event before the processor can, and the job queue has
    /// no such fast path. Raise it only to hold work back deliberately.
    /// </summary>
    [Range(0, 600)]
    public int ProcessingDelaySeconds { get; init; }

    /// <summary>
    /// Gets how long, in seconds, a processor replica's claim on a row lasts. Other replicas skip
    /// rows with an unexpired lease, so a command never runs on two replicas at once; if a replica
    /// dies mid-execution, its rows become claimable again once the lease expires. Set it comfortably
    /// above the longest expected handler duration, because a handler that outlives its lease can be
    /// started again elsewhere while it is still running.
    /// </summary>
    [Range(10, 3600)]
    public int LeaseSeconds { get; init; } = 300;

    /// <summary>
    /// Gets the base, in seconds, of the exponential backoff applied to a failed command before it is
    /// retried (attempt <c>n</c> waits <c>RetryBackoffBaseSeconds * 2^(n-1)</c>, multiplied by a
    /// random jitter factor in <c>[0.8, 1.2]</c> so commands that failed together do not retry in
    /// lockstep, then capped at <see cref="MaxRetryBackoffSeconds"/>).
    /// </summary>
    [Range(1, 3600)]
    public int RetryBackoffBaseSeconds { get; init; } = 10;

    /// <summary>
    /// Gets the ceiling, in seconds, on one retry backoff. Independent of
    /// <see cref="LeaseSeconds"/> (which is what the outbox caps its backoff at), because a job queue
    /// wants a long lease for slow handlers and a short ceiling on how long a transient failure
    /// parks a command.
    /// </summary>
    [Range(1, 86400)]
    public int MaxRetryBackoffSeconds { get; init; } = 600;

    /// <summary>
    /// Gets the number of days a <b>completed</b> row is retained before
    /// <c>InternalCommandCleanupService</c> purges it. Set to <c>0</c> to keep rows indefinitely.
    /// </summary>
    [Range(0, 3650)]
    public int RetentionDays { get; init; } = 7;

    /// <summary>
    /// Gets the number of days a <b>dead-lettered</b> row is retained. <c>0</c> (the default) falls
    /// back to <see cref="RetentionDays"/>. Set it higher to keep abandoned commands available for
    /// diagnosis and requeue.
    /// </summary>
    [Range(0, 3650)]
    public int DeadLetterRetentionDays { get; init; }

    /// <summary>
    /// Gets how often, in hours, <c>InternalCommandCleanupService</c> runs its purge sweep across
    /// every relational data source in use. Ignored when <see cref="RetentionDays"/> is <c>0</c>.
    /// </summary>
    [Range(1, 168)]
    public int CleanupIntervalHours { get; init; } = 6;

    /// <summary>
    /// Gets the engine of the data source whose <c>InternalCommands</c> table
    /// <c>IInternalCommandScheduler</c> writes to. Must be a relational provider (SQL Server or
    /// SQLite); Cosmos has no such table. The <em>processor</em> is not limited to this source: it
    /// drains the table of every relational physical source in use.
    /// </summary>
    public DataSource DataSource { get; init; } = DataSource.SQLServer;

    /// <summary>
    /// Gets the <b>logical</b> data source name (paired with <see cref="DataSource"/>) whose
    /// <c>InternalCommands</c> table receives scheduled rows. Defaults to
    /// <see cref="DataSourceKey.DefaultName"/>. Point it at the source holding the aggregates that
    /// schedule work, so the row and the aggregate change share one transaction.
    /// </summary>
    public string DatabaseName { get; init; } = DataSourceKey.DefaultName;
}
