using System.ComponentModel.DataAnnotations;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Configuration for the outbox background processor, bound from the <c>Outbox</c> section.
/// All properties have sensible defaults so the section is optional in <c>appsettings.json</c>.
/// </summary>
public sealed class OutboxSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Outbox";

    /// <summary>Gets the maximum number of outbox messages to process per polling cycle.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; init; } = 50;

    /// <summary>Gets the maximum number of retry attempts before a message is considered failed.</summary>
    [Range(1, 20)]
    public int MaxRetries { get; init; } = 5;

    /// <summary>
    /// Gets the fallback polling interval in seconds. With signal-based wakeup, this acts as
    /// a safety net — the outbox processor normally wakes immediately on new entries, and when
    /// it sees pending-but-not-yet-eligible messages it smart-waits only until the earliest one
    /// becomes eligible. Deployed environments can therefore set this high (e.g. 300) to cut
    /// idle polling without adding latency for real messages.
    /// </summary>
    [Range(1, 3600)]
    public int PollingIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// Gets the delay in seconds after message creation before it becomes eligible for processing.
    /// This bounds the duplicate-dispatch window: the in-process pipeline (save → dispatch → mark
    /// processed) must complete within this delay or the processor may re-dispatch the event.
    /// Handlers are required to be idempotent regardless (at-least-once delivery).
    /// </summary>
    [Range(0, 600)]
    public int ProcessingDelaySeconds { get; init; } = 5;

    /// <summary>
    /// Gets the engine of the data source where integration events published via
    /// <c>IEventBus</c> are written to the outbox. Must be a relational provider that
    /// supports the outbox table (SQL Server or SQLite).
    /// Defaults to <see cref="DataSource.SQLServer"/>.
    /// </summary>
    public DataSource DataSource { get; init; } = DataSource.SQLServer;

    /// <summary>
    /// Gets the <b>logical</b> data source name (paired with <see cref="DataSource"/>) where
    /// integration events published via <c>IEventBus</c> are written to the outbox.
    /// Defaults to <see cref="DataSourceKey.DefaultName"/> (the top-level connection strings),
    /// preserving single-database behavior. The outbox <em>processor</em> is not limited to this
    /// source — it drains the outbox table of every relational physical source in use.
    /// </summary>
    public string DatabaseName { get; init; } = DataSourceKey.DefaultName;

    /// <summary>
    /// Gets the number of days a <b>processed</b> outbox message is retained before
    /// <c>OutboxCleanupService</c> purges it. Set to <c>0</c> to disable purging (rows are kept
    /// indefinitely — the pre-1.x behavior). Defaults to <c>7</c>.
    /// </summary>
    [Range(0, 3650)]
    public int RetentionDays { get; init; } = 7;

    /// <summary>
    /// Gets how often, in hours, <c>OutboxCleanupService</c> runs its purge sweep across every
    /// relational data source in use. Ignored when <see cref="RetentionDays"/> is <c>0</c>.
    /// Defaults to <c>6</c>.
    /// </summary>
    [Range(1, 168)]
    public int CleanupIntervalHours { get; init; } = 6;

    /// <summary>
    /// Gets how long, in seconds, a processor replica's claim on a batch of outbox rows lasts.
    /// Other replicas skip rows with an unexpired lease, so concurrent replicas never
    /// double-dispatch; if a replica dies mid-batch, its rows become claimable again once the
    /// lease expires. Must comfortably exceed the time to dispatch one batch. Defaults to <c>300</c>.
    /// </summary>
    [Range(10, 3600)]
    public int LeaseSeconds { get; init; } = 300;

    /// <summary>
    /// Gets the number of days a <b>dead-lettered</b> outbox message (retries exhausted, never
    /// delivered) is retained before <c>OutboxCleanupService</c> purges it. <c>0</c> (the default)
    /// falls back to <see cref="RetentionDays"/>. Set it higher than <see cref="RetentionDays"/>
    /// to keep failed payloads around longer for diagnosis and manual replay.
    /// </summary>
    [Range(0, 3650)]
    public int DeadLetterRetentionDays { get; init; }
}
