using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace MMCA.Common.Aspire.Telemetry;

/// <summary>
/// The two instrumentation predicates behind the <c>Telemetry:FilterProbeTelemetry</c> cost knob
/// (rubric §31). Health probes are pure infrastructure chatter: Container Apps liveness and
/// readiness probes, the gateway's downstream aggregate probes, YARP active health checks and the
/// availability web test together accounted for every AppRequests row in both production
/// workspaces, and none of them carries end-user signal.
/// <para>
/// <see cref="ShouldCollectRequest"/> drops the inbound probe request span, and stamps
/// <see cref="ProbeMarkerTagName"/> on the request activity on its way out. The marker is what
/// <see cref="ProbeTelemetryFilterProcessor"/> matches on: when this predicate refuses a request the
/// ASP.NET Core instrumentation returns before it writes <c>url.path</c>, so the descendants of that
/// request (the SQL <c>SELECT 1</c>, the Redis PING) would otherwise have no way to recognize their
/// own probe ancestor.
/// </para>
/// <para>
/// <see cref="ShouldCollectOutgoing"/> drops outbound probe calls that are NOT descendants of an
/// inbound request and therefore never reach the processor at all: the gateway's
/// <c>DownstreamServiceHealthCheck</c> calls to each backend's <c>/alive</c> and YARP's active
/// health checks, both driven by background timers.
/// </para>
/// </summary>
internal static class ProbeTelemetryFilter
{
    /// <summary>
    /// Tag stamped on an inbound probe request activity so descendant spans can recognize it. Never
    /// exported: the activity carrying it is unrecorded by the same pass that stamps it.
    /// </summary>
    internal const string ProbeMarkerTagName = "mmca.probe";

    /// <summary>
    /// Whether an inbound request should be traced. Probe endpoints are refused (and marked).
    /// </summary>
    /// <param name="context">The request being started.</param>
    /// <returns><see langword="false"/> for a health-probe request, otherwise <see langword="true"/>.</returns>
    internal static bool ShouldCollectRequest(HttpContext context)
    {
        if (context is null || !HealthEndpointPaths.IsProbePath(context.Request.Path.Value))
        {
            return true;
        }

        // Activity.Current is the request activity here: the instrumentation invokes this filter
        // from its start callback, after ASP.NET Core has started and made it current.
        if (Activity.Current is { Kind: ActivityKind.Server } request)
        {
            request.SetTag(ProbeMarkerTagName, true);
        }

        return false;
    }

    /// <summary>
    /// Whether an outbound HTTP call should be traced. Calls to a probe endpoint are refused.
    /// </summary>
    /// <param name="request">The outgoing request.</param>
    /// <returns><see langword="false"/> for a health-probe call, otherwise <see langword="true"/>.</returns>
    internal static bool ShouldCollectOutgoing(HttpRequestMessage request)
        => request?.RequestUri is not { } uri || !HealthEndpointPaths.IsProbePath(PathOf(uri));

    /// <summary>
    /// The path portion of an outgoing request URI. Service discovery hands the client an absolute
    /// URI, but a relative one is legal on a client with a BaseAddress, so both shapes are handled
    /// without allocating a combined URI on every outbound call.
    /// </summary>
    /// <param name="uri">The request URI.</param>
    /// <returns>The path, with any query or fragment removed.</returns>
    private static string PathOf(Uri uri)
    {
        if (uri.IsAbsoluteUri)
        {
            return uri.AbsolutePath;
        }

        var raw = uri.OriginalString;
        var cut = raw.AsSpan().IndexOfAny('?', '#');
        return cut < 0 ? raw : raw[..cut];
    }
}
