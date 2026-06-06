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
    /// a safety net — the outbox processor normally wakes immediately on new entries.
    /// </summary>
    [Range(1, 300)]
    public int PollingIntervalSeconds { get; init; } = 2;

    /// <summary>Gets the delay in seconds after message creation before it becomes eligible for processing.</summary>
    [Range(0, 600)]
    public int ProcessingDelaySeconds { get; init; } = 30;

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
}
