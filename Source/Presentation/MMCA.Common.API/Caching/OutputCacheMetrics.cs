using System.Diagnostics.Metrics;

namespace MMCA.Common.API.Caching;

/// <summary>
/// OpenTelemetry instruments for the output-cache eviction consumer. A host exports them by
/// registering the <see cref="MeterName"/> meter; the Aspire service defaults
/// (<c>ConfigureOpenTelemetry</c>) already do. The meter name is duplicated as a literal in
/// MMCA.Common.Aspire because that package has no reference to MMCA.Common.API.
/// <para>
/// One meter serves every output-cache instrument. Never create a second <see cref="Meter"/> with
/// this name: a duplicate instance publishes a parallel set of instruments under the same meter
/// name, and a listener enabling one of them silently misses measurements recorded on the other.
/// </para>
/// </summary>
internal static class OutputCacheMetrics
{
    /// <summary>OpenTelemetry meter name for output-cache metrics.</summary>
    internal const string MeterName = "MMCA.Common.OutputCache";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Tags the eviction consumer failed to evict, tagged by <c>cache_tag</c>. A non-zero rate means
    /// this host is serving output-cached responses it was told to drop, so it is the alert target
    /// for cross-service cache coherence. Cardinality is bounded by the host's own tag vocabulary,
    /// which is a small fixed set declared in its output-cache policies.
    /// </summary>
    internal static readonly Counter<long> EvictionFailed = Meter.CreateCounter<long>(
        "cache.eviction.failed",
        unit: "{tag}",
        description: "Count of output-cache tags the eviction consumer failed to evict, tagged by cache tag.");

    /// <summary>Records one failed tag eviction.</summary>
    /// <param name="cacheTag">The output-cache tag that could not be evicted.</param>
    internal static void RecordEvictionFailure(string cacheTag) =>
        EvictionFailed.Add(1, new KeyValuePair<string, object?>("cache_tag", cacheTag));
}
