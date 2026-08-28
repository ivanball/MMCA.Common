using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace MMCA.Common.Gateway;

/// <summary>
/// The framework's forwarded-headers wiring, reachable from a gateway host.
/// <para>
/// Service hosts get this step from <c>UseCommonMiddlewarePipeline</c> in <c>MMCA.Common.API</c>,
/// but a gateway takes none of that package (no controllers, no DbContext, no auth middleware), so
/// it lives here too: the one package every MMCA gateway already references, and one whose only
/// runtime dependency is YARP. Without it a gateway hand-rolls the same five lines, and getting them
/// wrong is not visible until production: behind Azure Container Apps ingress every connection
/// arrives from the ingress proxy's IP, so an unforwarded gateway collapses the per-client-IP rate
/// limit partition into ONE shared window for every real user.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with an extension(T) block in a static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class ForwardedHeadersExtensions
{
    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// Applies <c>X-Forwarded-For</c> / <c>-Proto</c> / <c>-Host</c> with the framework's
        /// options, which is the same shape the service-side pipeline's <c>ForwardedHeaders</c> step
        /// uses. Call it FIRST in a gateway pipeline, before the edge rate limiter: the limiter
        /// partitions on the client IP and can only see the real one once the headers have been
        /// applied.
        /// </summary>
        /// <returns>The same application builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
        public IApplicationBuilder UseCommonForwardedHeaders()
        {
            ArgumentNullException.ThrowIfNull(app);

            return app.UseForwardedHeaders(CreateForwardedHeadersOptions());
        }
    }

    /// <summary>
    /// Builds the framework's <see cref="ForwardedHeadersOptions"/>: the three
    /// <c>X-Forwarded-*</c> headers, with the known-proxy and known-network allow-lists CLEARED.
    /// <para>
    /// Clearing them is deliberate and load-bearing. Cloud reverse proxies (Azure Container Apps,
    /// AWS ALB) front the app from internal IPs that are in neither default allow-list, so leaving
    /// the defaults in place makes the middleware ignore every forwarded header it receives and
    /// report the ingress IP as the client.
    /// </para>
    /// </summary>
    /// <returns>The configured options.</returns>
    public static ForwardedHeadersOptions CreateForwardedHeadersOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
        };

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        return options;
    }
}
