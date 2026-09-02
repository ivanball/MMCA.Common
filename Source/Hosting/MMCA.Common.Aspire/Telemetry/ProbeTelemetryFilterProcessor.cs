using System.Diagnostics;
using OpenTelemetry;

namespace MMCA.Common.Aspire.Telemetry;

/// <summary>
/// Suppresses the descendants of a health-probe request from telemetry export: the SQL
/// <c>SELECT 1</c> of the database health check, the Redis PING, and the gateway's HttpClient call
/// to each backend's <c>/alive</c>. The probe request span itself is refused by
/// <see cref="ProbeTelemetryFilter.ShouldCollectRequest"/>, but its children are sampled
/// independently and would otherwise still be exported, which is how they came to make up most of
/// the AppDependencies volume in both production workspaces (rubric §31).
/// <para>
/// Registered only when <c>Telemetry:FilterProbeTelemetry</c> is left at its default of
/// <see langword="true"/>, and always before the exporters so the cleared Recorded flag is what
/// their batch processors see. Metrics are untouched: <c>http.server.request.duration</c>, Kestrel
/// and routing instruments keep flowing, so probe traffic stays visible on dashboards.
/// </para>
/// </summary>
public sealed class ProbeTelemetryFilterProcessor : BaseProcessor<Activity>
{
    // Set by the ASP.NET Core instrumentation on the server span at request start, before any child
    // span exists. Names, not the semantic-convention constants, because this package deliberately
    // takes no dependency on the instrumentation's internal attribute class.
    private const string UrlPathTagName = "url.path";
    private const string HttpRouteTagName = "http.route";

    /// <inheritdoc />
    public override void OnStart(Activity data) => SuppressWhenUnderProbe(data);

    /// <inheritdoc />
    /// <remarks>
    /// The same pass runs again at end because a span's identifying tags do not all exist at start:
    /// a client span is started before its instrumentation has written any attribute, so an
    /// OnStart-only filter would depend on callback ordering it does not control. Clearing Recorded
    /// at end is what actually keeps the span out of the exporters (the pattern
    /// <see cref="OutboxPollFilterProcessor"/> uses), while the OnStart pass additionally stops the
    /// instrumentation from collecting data it will never export.
    /// </remarks>
    public override void OnEnd(Activity data) => SuppressWhenUnderProbe(data);

    private static void SuppressWhenUnderProbe(Activity data)
    {
        if (data is null)
        {
            // Never throw from a telemetry callback.
            return;
        }

        // Walk the in-process parent chain: a probe's dependency spans can sit several levels below
        // the request span (HttpClient handler -> connection -> DNS).
        for (var current = data; current is not null; current = current.Parent)
        {
            if (IsProbeRequest(current))
            {
                // Clearing Recorded makes the batch export processors (Azure Monitor, OTLP) skip
                // this activity; dropping IsAllDataRequested stops instrumentation from enriching
                // it in the first place.
                data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                data.IsAllDataRequested = false;
                return;
            }
        }
    }

    private static bool IsProbeRequest(Activity activity)
    {
        if (activity.GetTagItem(ProbeTelemetryFilter.ProbeMarkerTagName) is not null)
        {
            return true;
        }

        // Server kind only: an outgoing call to a probe endpoint is the gateway's own health check,
        // and is filtered at the instrumentation instead, so a normal request never loses its whole
        // subtree because one dependency happened to be a probe.
        return activity.Kind == ActivityKind.Server
            && (HealthEndpointPaths.IsProbePath(activity.GetTagItem(UrlPathTagName) as string)
                || HealthEndpointPaths.IsProbePath(activity.GetTagItem(HttpRouteTagName) as string)
                || HealthEndpointPaths.IsProbePath(RouteOfDisplayName(activity.DisplayName)));
    }

    /// <summary>
    /// The route portion of a server span's display name. Once the route is resolved the ASP.NET
    /// Core instrumentation renames the span to <c>"{method} {route}"</c>, which is the only probe
    /// evidence left on a span whose tags an exporter-side enricher has already consumed.
    /// </summary>
    /// <param name="displayName">The activity display name.</param>
    /// <returns>The trailing route segment, or the whole name when there is no method prefix.</returns>
    private static string RouteOfDisplayName(string displayName)
    {
        var lastSpace = displayName.LastIndexOf(' ');
        return lastSpace < 0 ? displayName : displayName[(lastSpace + 1)..];
    }
}
