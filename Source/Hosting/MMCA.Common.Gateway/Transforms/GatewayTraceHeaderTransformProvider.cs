using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MMCA.Common.Gateway.Transforms;

/// <summary>
/// Stamps the matched route id and target cluster id onto every proxied request, so a downstream
/// log line, trace or access log can name the gateway route that produced it instead of being
/// correlated back by path pattern (which stops working the moment two routes share a prefix).
/// <para>
/// The inbound value is REMOVED before the gateway's own is written. Without that, a client could
/// send <c>X-MMCA-Route</c> itself and a downstream would attribute its request to a route it never
/// matched. A header a service trusts must be one only the gateway can set.
/// </para>
/// </summary>
/// <param name="options">The bound gateway settings.</param>
public sealed class GatewayTraceHeaderTransformProvider(IOptions<GatewaySettings> options) : ITransformProvider
{
    private readonly GatewayTraceHeaderSettings _settings =
        (options?.Value ?? throw new ArgumentNullException(nameof(options))).TraceHeaders;

    /// <summary>No route-level validation: the transform takes no operator-supplied parameters.</summary>
    /// <param name="context">The route validation context.</param>
    public void ValidateRoute(TransformRouteValidationContext context)
    {
        // Intentionally empty: this provider is applied to every route unconditionally and reads
        // nothing from the route's own transform list, so there is nothing an operator can mistype.
    }

    /// <summary>No cluster-level validation, for the same reason as <see cref="ValidateRoute"/>.</summary>
    /// <param name="context">The cluster validation context.</param>
    public void ValidateCluster(TransformClusterValidationContext context)
    {
        // Intentionally empty. See ValidateRoute.
    }

    /// <summary>Adds the stamping request transform to every route.</summary>
    /// <param name="context">The transform builder context for one route.</param>
    public void Apply(TransformBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_settings.Enabled)
        {
            return;
        }

        // Captured once at build time: the ids are fixed for the lifetime of this route's transform
        // pipeline, so resolving them per request would be work with no possible different answer.
        var routeHeader = _settings.RouteHeaderName;
        var clusterHeader = _settings.ClusterHeaderName;
        var routeId = context.Route.RouteId;
        var clusterId = context.Route.ClusterId;

        context.AddRequestTransform(transformContext =>
        {
            var headers = transformContext.ProxyRequest.Headers;

            headers.Remove(routeHeader);
            headers.Remove(clusterHeader);

            if (!string.IsNullOrEmpty(routeId))
            {
                headers.TryAddWithoutValidation(routeHeader, routeId);
            }

            if (!string.IsNullOrEmpty(clusterId))
            {
                headers.TryAddWithoutValidation(clusterHeader, clusterId);
            }

            return ValueTask.CompletedTask;
        });
    }
}
