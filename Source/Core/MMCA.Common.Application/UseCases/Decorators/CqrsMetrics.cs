using System.Diagnostics.Metrics;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// RED (Rate / Errors / Duration) metrics for the CQRS pipeline, emitted by the logging
/// decorators. Count gives rate, the <c>outcome</c> tag gives errors, and the histogram gives
/// duration. A host exports these by registering the <see cref="MeterName"/> meter (the Aspire
/// service defaults, <c>ConfigureOpenTelemetry</c>, do this). The meter name is duplicated as a
/// literal in MMCA.Common.Aspire because that package has no reference to Application.
/// <para>
/// The caching query decorator adds the cache hit/miss counters below, so a host can chart the
/// hit ratio per query and spot a cache that has stopped serving reads.
/// </para>
/// <para>
/// The authorization and timeout decorators add two short-circuit counters, so a permission that
/// is denying far more traffic than expected, or a handler that keeps exhausting its execution
/// budget, is visible as a metric rather than only as a client-side error rate.
/// </para>
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

    /// <summary>Cacheable queries served from the cache without executing the handler, tagged by <c>query</c>.</summary>
    internal static readonly Counter<long> QueryCacheHits = Meter.CreateCounter<long>(
        "cqrs.query.cache.hit",
        unit: "{query}",
        description: "Count of cacheable queries served from the cache, tagged by query name.");

    /// <summary>Cacheable queries that fell through to the handler, tagged by <c>query</c>.</summary>
    internal static readonly Counter<long> QueryCacheMisses = Meter.CreateCounter<long>(
        "cqrs.query.cache.miss",
        unit: "{query}",
        description: "Count of cacheable queries that executed the handler because the cache did not serve them, tagged by query name.");

    /// <summary>Requests short-circuited by the authorization decorators, tagged by <c>request_type</c>.</summary>
    internal static readonly Counter<long> AuthorizationDenied = Meter.CreateCounter<long>(
        "cqrs.authorization.denied.count",
        unit: "{request}",
        description: "Count of commands and queries denied by the authorization decorators, tagged by request type name.");

    /// <summary>Requests abandoned because their own execution budget expired, tagged by <c>request_type</c>.</summary>
    internal static readonly Counter<long> TimeoutExpired = Meter.CreateCounter<long>(
        "cqrs.timeout.count",
        unit: "{request}",
        description: "Count of commands and queries whose IHasTimeout budget expired before the handler completed, tagged by request type name.");

    /// <summary>Records one cache hit for the named query.</summary>
    /// <param name="queryName">The query type name.</param>
    internal static void RecordCacheHit(string queryName) =>
        QueryCacheHits.Add(1, new KeyValuePair<string, object?>("query", queryName));

    /// <summary>Records one cache miss for the named query.</summary>
    /// <param name="queryName">The query type name.</param>
    internal static void RecordCacheMiss(string queryName) =>
        QueryCacheMisses.Add(1, new KeyValuePair<string, object?>("query", queryName));

    /// <summary>Records one authorization denial for the named command or query.</summary>
    /// <param name="requestTypeName">The command or query type name.</param>
    internal static void RecordAuthorizationDenied(string requestTypeName) =>
        AuthorizationDenied.Add(1, new KeyValuePair<string, object?>("request_type", requestTypeName));

    /// <summary>Records one expired execution budget for the named command or query.</summary>
    /// <param name="requestTypeName">The command or query type name.</param>
    internal static void RecordTimeout(string requestTypeName) =>
        TimeoutExpired.Add(1, new KeyValuePair<string, object?>("request_type", requestTypeName));
}
