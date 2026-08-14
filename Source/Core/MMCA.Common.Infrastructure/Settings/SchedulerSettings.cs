using System.ComponentModel.DataAnnotations;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Configuration for the recurring job scheduler, bound from the <c>Scheduler</c> section.
/// Every property has a default, so a host that opts in only needs <c>Scheduler:Enabled</c>.
/// </summary>
/// <remarks>
/// <see cref="Enabled"/> is the single gate: it decides both whether the runner does any work and
/// whether the <c>ScheduledJobs</c> table is mapped into the model at all. A host that leaves it
/// false has exactly the model it had before the scheduler existed, so its migrations never see
/// the table.
/// </remarks>
public sealed class SchedulerSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Scheduler";

    /// <summary>
    /// Gets a value indicating whether the scheduler is active. Defaults to <see langword="false"/>:
    /// adopting the framework must never add a table or a background loop to a host that did not
    /// ask for one.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the fallback polling interval in seconds. The runner normally sleeps until the earliest
    /// due job (the smart wait), so this only bounds how long it can sleep when the next occurrence
    /// is far away, and how quickly it notices a configuration-driven schedule change.
    /// </summary>
    [Range(1, 3600)]
    public int PollingIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// Gets how long, in seconds, a replica's claim on a job row lasts. Other replicas skip rows
    /// with an unexpired lease, so concurrent replicas never run the same occurrence twice; if a
    /// replica dies mid-execution, the row becomes claimable again once the lease expires. Set it
    /// comfortably above the longest expected job duration.
    /// </summary>
    [Range(10, 3600)]
    public int LeaseSeconds { get; init; } = 300;

    /// <summary>
    /// Gets the engine whose <c>Default</c> database holds the <c>ScheduledJobs</c> table. Jobs are
    /// host-scoped, so there is exactly one table per host rather than one per data source; this
    /// only says which relational engine that one table lives on.
    /// Defaults to <see cref="DataSource.SQLServer"/>. Cosmos DB is not a valid value (the table is
    /// relational).
    /// </summary>
    public DataSource DataSource { get; init; } = DataSource.SQLServer;

    /// <summary>
    /// Gets the per-job configuration overrides, keyed by
    /// <see cref="Application.Interfaces.IScheduledJob.Name"/> and bound from
    /// <c>Scheduler:Jobs:{Name}</c>. An entry lets a host retime a job without touching code; an
    /// absent entry leaves the job's compiled-in default in force.
    /// </summary>
    public Dictionary<string, ScheduledJobOverrideSettings> Jobs { get; } = [];
}

/// <summary>
/// Per-job configuration overrides bound from <c>Scheduler:Jobs:{Name}</c>.
/// </summary>
public sealed class ScheduledJobOverrideSettings
{
    /// <summary>
    /// Gets the five-field UTC cron expression that replaces the job's compiled-in
    /// <see cref="Application.Interfaces.IScheduledJob.CronExpression"/>. Null or blank leaves the
    /// code default in force. A change here is picked up on the next scheduler cycle: the runner
    /// rewrites the stored expression and recomputes the next occurrence from the current instant.
    /// </summary>
    public string? Cron { get; init; }
}
