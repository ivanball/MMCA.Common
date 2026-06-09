using System.Diagnostics.Metrics;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// RED (Rate / Errors / Duration) metrics for the CQRS pipeline, emitted by the logging
/// decorators. Count gives rate, the <c>outcome</c> tag gives errors, and the histogram gives
/// duration. A host exports these by registering the <see cref="MeterName"/> meter — the Aspire
/// service defaults (<c>ConfigureOpenTelemetry</c>) do this. The meter name is duplicated as a
/// literal in MMCA.Common.Aspire because that package has no reference to Application.
/// </summary>
internal static class CqrsMetrics
{
    /// <summary>OpenTelemetry meter name for CQRS pipeline metrics.</summary>
    internal const string MeterName = "MMCA.Common.Cqrs";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Command-handling duration in milliseconds, tagged by <c>command</c> and <c>outcome</c>.</summary>
    internal static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "cqrs.command.duration",
        unit: "ms",
        description: "Duration of CQRS command handling, tagged by command name and outcome.");

    /// <summary>Query-handling duration in milliseconds, tagged by <c>query</c> and <c>outcome</c>.</summary>
    internal static readonly Histogram<double> QueryDuration = Meter.CreateHistogram<double>(
        "cqrs.query.duration",
        unit: "ms",
        description: "Duration of CQRS query handling, tagged by query name and outcome.");
}
