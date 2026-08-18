using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Application.Services;

/// <summary>
/// Runs a side effect that must never fail its caller: cache eviction after a committed command,
/// a fire-and-forget notification, an eviction broadcast onto the bus. The action is awaited, and
/// any failure is turned into exactly one Warning log plus one metric increment instead of an
/// exception that would roll back, retry, or 500 an operation whose real work already succeeded.
/// <para>
/// Cancellation is NOT swallowed. When the caller's own token is the reason the action stopped, the
/// <see cref="OperationCanceledException"/> is rethrown, so a host shutdown or an abandoned request
/// still unwinds promptly rather than being logged as a spurious failure. A best-effort side effect
/// that must outlive the request should be passed <see cref="CancellationToken.None"/> by its
/// caller, exactly as the caching decorators do.
/// </para>
/// <para>
/// Failures are counted on the <c>MMCA.Common.BestEffort</c> meter as
/// <c>besteffort.dispatch.failed</c>, tagged by <c>operation</c>, so a side effect that has quietly
/// stopped working is visible as a metric rather than only as a line in a log nobody reads. Keep
/// the operation name a low-cardinality constant: it becomes a metric tag.
/// </para>
/// </summary>
public static class BestEffort
{
    /// <summary>
    /// Awaits <paramref name="action"/> and swallows any non-cancellation failure, logging it once
    /// at Warning and counting it on the best-effort meter.
    /// </summary>
    /// <param name="operation">
    /// Short, low-cardinality name of the side effect (for example <c>"output-cache-evict"</c>). It
    /// appears in the Warning message and as the <c>operation</c> metric tag.
    /// </param>
    /// <param name="logger">Logger used for the single Warning emitted on failure.</param>
    /// <param name="action">The side effect to run.</param>
    /// <param name="cancellationToken">
    /// Token passed to <paramref name="action"/>. When it is the reason the action stopped, the
    /// cancellation is rethrown rather than swallowed.
    /// </param>
    /// <returns>A task that completes when the action has run or its failure has been recorded.</returns>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> or <paramref name="action"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task ExecuteAsync(
        string operation,
        ILogger logger,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked to stop. Rethrow so shutdown/abandonment stays a cancellation
            // rather than being recorded as a best-effort failure.
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately broad (CA1031/S2221 are suggestions here): the whole contract of this
            // helper is that NOTHING the side effect throws reaches the caller.
            BestEffortMetrics.RecordFailure(operation);
            BestEffortLog.DispatchFailed(logger, operation, ex);
        }
    }
}

/// <summary>
/// Source-generated log messages for <see cref="BestEffort"/>. Separate companion type so the
/// public helper does not have to be partial.
/// </summary>
internal static partial class BestEffortLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Best-effort operation '{Operation}' failed and was swallowed; the caller's outcome is unaffected")]
    internal static partial void DispatchFailed(ILogger logger, string operation, Exception exception);
}

/// <summary>
/// OpenTelemetry instrument for <see cref="BestEffort"/>. A host exports it by registering the
/// <see cref="MeterName"/> meter; the Aspire service defaults (<c>ConfigureOpenTelemetry</c>)
/// already do. The meter name is duplicated as a literal in MMCA.Common.Aspire because that
/// package has no reference to Application.
/// <para>
/// It is a meter of its own rather than a counter folded into <c>MMCA.Common.Cqrs</c>: best-effort
/// dispatch is not part of the CQRS pipeline (hosts call it from handlers, hosted services and
/// consumers alike), and a separate meter lets an operator drop or keep it independently of the
/// RED metrics.
/// </para>
/// </summary>
internal static class BestEffortMetrics
{
    /// <summary>OpenTelemetry meter name for best-effort dispatch metrics.</summary>
    internal const string MeterName = "MMCA.Common.BestEffort";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Best-effort side effects that failed and were swallowed, tagged by <c>operation</c>.</summary>
    internal static readonly Counter<long> DispatchFailed = Meter.CreateCounter<long>(
        "besteffort.dispatch.failed",
        unit: "{operation}",
        description: "Count of best-effort side effects that failed and were swallowed, tagged by operation name.");

    /// <summary>Records one swallowed failure for the named operation.</summary>
    /// <param name="operation">The low-cardinality operation name.</param>
    internal static void RecordFailure(string operation) =>
        DispatchFailed.Add(1, new KeyValuePair<string, object?>("operation", operation));
}
