using System.Diagnostics.Metrics;

namespace MMCA.Common.API.Idempotency;

/// <summary>
/// Counters for the idempotency filter: how often a stored response is replayed, how often a
/// duplicate is rejected, and how often the filter degrades because its cache or lock is unhealthy.
/// A host exports these by registering the <see cref="MeterName"/> meter, which the Aspire service
/// defaults do. The meter name is duplicated as a literal in MMCA.Common.Aspire because that
/// package has no reference to this one.
/// </summary>
/// <remarks>
/// The filter never touches the instruments directly: it calls the helpers below, so the tag names
/// and instrument names live in exactly one place.
/// </remarks>
internal static class IdempotencyMetrics
{
    /// <summary>OpenTelemetry meter name for the idempotency instruments.</summary>
    internal const string MeterName = "MMCA.Common.Idempotency";

    /// <summary>
    /// Conflict tag value for a key replayed with a payload different from the stored one.
    /// </summary>
    internal const string ConflictKindBodyMismatch = "body_mismatch";

    /// <summary>
    /// Conflict tag value for a duplicate that arrived while the original was still executing.
    /// </summary>
    internal const string ConflictKindInFlight = "in_flight";

    /// <summary>Tag name carrying which of the two conflict shapes was recorded.</summary>
    private const string ConflictKindTag = "kind";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> ReplayedCounter = Meter.CreateCounter<long>(
        "idempotency.replayed",
        unit: "{request}",
        description: "Requests answered from the idempotency cache instead of executing the action.");

    private static readonly Counter<long> ConflictCounter = Meter.CreateCounter<long>(
        "idempotency.conflict",
        unit: "{request}",
        description: "Duplicate requests rejected, tagged by kind (body_mismatch or in_flight).");

    private static readonly Counter<long> DegradedCounter = Meter.CreateCounter<long>(
        "idempotency.degraded",
        unit: "{request}",
        description: "Requests that ran without the idempotency guarantee because the cache or lock faulted.");

    /// <summary>Records that a stored response was replayed to a duplicate request.</summary>
    internal static void RecordReplayed() => ReplayedCounter.Add(1);

    /// <summary>
    /// Records that a duplicate was rejected rather than replayed or executed.
    /// </summary>
    /// <param name="kind">
    /// <see cref="ConflictKindBodyMismatch"/> for a key reused with a different payload,
    /// <see cref="ConflictKindInFlight"/> for a duplicate whose original is still running.
    /// </param>
    internal static void RecordConflict(string kind) =>
        ConflictCounter.Add(1, new KeyValuePair<string, object?>(ConflictKindTag, kind));

    /// <summary>
    /// Records that a cache or lock fault was swallowed and the request proceeded without the
    /// idempotency guarantee. A sustained non-zero rate here means deduplication is effectively off.
    /// </summary>
    internal static void RecordDegraded() => DegradedCounter.Add(1);
}
