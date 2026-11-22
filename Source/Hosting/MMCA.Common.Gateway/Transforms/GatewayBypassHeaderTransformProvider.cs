using Microsoft.Extensions.Configuration;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MMCA.Common.Gateway.Transforms;

/// <summary>
/// Removes the edge limiter's trusted-caller and synthetic-traffic headers from every proxied
/// request, so the secrets they carry end at the gateway and no downstream service, sidecar or
/// header log ever receives them. The limiter reads both headers before the proxy runs, so its
/// decisions are unaffected.
/// <para>
/// The header names come from the <c>GatewayRateLimiting</c> section the edge limiter of
/// <c>MMCA.Common.Aspire</c> binds (<c>TrustedCallerHeaderName</c>, <c>SyntheticTrafficHeaderName</c>),
/// read here by name because this package takes no reference to that one; an absent value falls
/// back to the same default the limiter uses.
/// </para>
/// </summary>
/// <param name="configuration">The application configuration; <see langword="null"/> uses the default names.</param>
internal sealed class GatewayBypassHeaderTransformProvider(IConfiguration? configuration = null) : ITransformProvider
{
    /// <summary>The edge limiter's configuration section (<c>GatewayRateLimitingSettings.SectionName</c>).</summary>
    internal const string RateLimitingSectionName = "GatewayRateLimiting";

    /// <summary>The limiter's default trusted-caller header name.</summary>
    internal const string DefaultTrustedCallerHeaderName = "X-Internal-Caller-Key";

    /// <summary>The limiter's default synthetic-traffic header name.</summary>
    internal const string DefaultSyntheticTrafficHeaderName = "X-Synthetic-Traffic-Key";

    private readonly string[] _headerNames = ResolveHeaderNames(configuration);

    /// <summary>No route-level validation: the transform takes no route parameters.</summary>
    /// <param name="context">The route validation context.</param>
    public void ValidateRoute(TransformRouteValidationContext context)
    {
        // Intentionally empty: applied to every route unconditionally, reading nothing from the
        // route's own transform list.
    }

    /// <summary>No cluster-level validation, for the same reason as <see cref="ValidateRoute"/>.</summary>
    /// <param name="context">The cluster validation context.</param>
    public void ValidateCluster(TransformClusterValidationContext context)
    {
        // Intentionally empty. See ValidateRoute.
    }

    /// <summary>Adds the header-removing request transform to every route.</summary>
    /// <param name="context">The transform builder context for one route.</param>
    public void Apply(TransformBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headerNames = _headerNames;
        context.AddRequestTransform(transformContext =>
        {
            foreach (var headerName in headerNames)
            {
                transformContext.ProxyRequest.Headers.Remove(headerName);
            }

            return ValueTask.CompletedTask;
        });
    }

    private static string[] ResolveHeaderNames(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(RateLimitingSectionName);

        return
        [
            .. new[]
            {
                NameOrDefault(section?["TrustedCallerHeaderName"], DefaultTrustedCallerHeaderName),
                NameOrDefault(section?["SyntheticTrafficHeaderName"], DefaultSyntheticTrafficHeaderName),
            }.Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static string NameOrDefault(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
}
