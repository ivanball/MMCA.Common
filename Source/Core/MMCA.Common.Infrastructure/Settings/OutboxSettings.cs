using System.ComponentModel.DataAnnotations;

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

    /// <summary>Gets the interval in seconds between polling cycles for unprocessed messages.</summary>
    [Range(1, 300)]
    public int PollingIntervalSeconds { get; init; } = 10;

    /// <summary>Gets the delay in seconds after message creation before it becomes eligible for processing.</summary>
    [Range(0, 600)]
    public int ProcessingDelaySeconds { get; init; } = 30;
}
