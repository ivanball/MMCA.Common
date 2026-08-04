using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace MMCA.Common.Aspire.Kestrel;

/// <summary>
/// Kestrel endpoint wiring for an extracted service host: one protocol profile for the default
/// endpoints plus an optional dedicated HTTP/1.1 listener for platform health probes.
/// <para>
/// Two operational facts drive this. First, a service that serves inbound gRPC on cleartext must
/// answer HTTP/2 with prior knowledge (h2c): there is no TLS, so there is no ALPN to negotiate
/// with, and the typed gRPC clients from <c>MMCA.Common.Grpc</c> speak h2c directly. Second, the
/// Azure Container Apps <c>httpGet</c> probes speak HTTP/1.1, which an Http2-only endpoint rejects
/// with GOAWAY <c>HTTP_1_1_REQUIRED</c>: that is why those probes used to be TCP-only and never
/// consulted the real, dependency-aware health checks. A separate Http1-only listener on a port
/// that is never published through ingress gives the platform a probe target while the service
/// endpoints stay on their own profile, and <c>MapDefaultEndpoints()</c> maps <c>/health</c>,
/// <c>/alive</c> and <c>/health/ready</c> on every listener, so the probe listener serves the real
/// health pipeline.
/// </para>
/// </summary>
public static class KestrelEndpointExtensions
{
    /// <summary>
    /// Configuration key carrying the dedicated health-probe port. Deployment infrastructure injects
    /// it as <c>HealthProbe__Port</c>; it is deliberately absent locally so Aspire's dynamic ports
    /// keep working and co-hosted services cannot collide on one machine.
    /// </summary>
    public const string HealthProbePortConfigKey = "HealthProbe:Port";

    /// <summary>
    /// The cleartext port a containerized service listens on (the platform's own default, which
    /// explicit <c>Listen</c> calls otherwise override).
    /// </summary>
    public const int DefaultCleartextPort = 8080;

    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        /// Applies <paramref name="defaultProtocols"/> to every Kestrel endpoint default and, when
        /// <see cref="HealthProbePortConfigKey"/> is configured, adds a dedicated Http1-only listener
        /// for the platform health probes.
        /// <para>
        /// Both deployed profiles are this one call with a different protocol set:
        /// </para>
        /// <list type="bullet">
        ///   <item>REST/gRPC services pass <see cref="HttpProtocols.Http2"/> and keep
        ///   <paramref name="redeclareCleartextEndpoint"/> at its default. Explicit
        ///   <c>Listen</c> calls override the container's <c>ASPNETCORE_HTTP_PORTS</c> default
        ///   binding, so the main h2c endpoint has to be re-declared alongside the probe port.</item>
        ///   <item>A host with config-declared Kestrel endpoints (for example a SignalR host running
        ///   the mixed profile: an <c>Http1AndHttp2</c> endpoint for the WebSocket Upgrade handshake
        ///   plus an Http2-only gRPC endpoint, both from <c>appsettings.json</c>) passes
        ///   <see cref="HttpProtocols.Http1AndHttp2"/> and <paramref name="redeclareCleartextEndpoint"/>
        ///   <see langword="false"/>. Config-declared endpoints and explicit <c>Listen</c> calls
        ///   coexist, so the probe listener is then strictly additive and nothing re-binds a port
        ///   the configuration already owns.</item>
        /// </list>
        /// </summary>
        /// <param name="defaultProtocols">Protocols applied to the endpoint defaults, and to the
        /// re-declared cleartext endpoint when there is one.</param>
        /// <param name="redeclareCleartextEndpoint">
        /// When <see langword="true"/> (the default) and a probe port is configured, re-declares the
        /// main cleartext listener so the explicit probe <c>Listen</c> call does not silently replace
        /// it. Pass <see langword="false"/> for a host whose endpoints come from configuration.
        /// </param>
        /// <param name="cleartextPort">The main cleartext port to re-declare (defaults to
        /// <see cref="DefaultCleartextPort"/>).</param>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// The configured health-probe port is not an integer. Failing at startup is deliberate: a
        /// mistyped probe port that silently produced no listener would leave the platform probing a
        /// closed port and the revision would never come up.
        /// </exception>
        public WebApplicationBuilder ConfigureEndpointsWithHealthProbe(
            HttpProtocols defaultProtocols,
            bool redeclareCleartextEndpoint = true,
            int cleartextPort = DefaultCleartextPort)
        {
            ArgumentNullException.ThrowIfNull(builder);

            var listeners = BuildListenerPlan(
                builder.Configuration,
                defaultProtocols,
                redeclareCleartextEndpoint,
                cleartextPort);

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = defaultProtocols);

                foreach (var listener in listeners)
                {
                    kestrel.ListenAnyIP(listener.Port, endpoint => endpoint.Protocols = listener.Protocols);
                }
            });

            return builder;
        }
    }

    /// <summary>
    /// Computes the explicit listeners to declare. Empty when no health-probe port is configured
    /// (the local and test case: endpoint defaults alone, so Aspire's dynamic ports keep working).
    /// </summary>
    /// <param name="configuration">Configuration carrying <see cref="HealthProbePortConfigKey"/>.</param>
    /// <param name="defaultProtocols">Protocols for the re-declared cleartext endpoint.</param>
    /// <param name="redeclareCleartextEndpoint">Whether the cleartext endpoint is re-declared.</param>
    /// <param name="cleartextPort">The main cleartext port.</param>
    /// <returns>The listeners to declare, in declaration order.</returns>
    internal static IReadOnlyList<KestrelListenerSpec> BuildListenerPlan(
        IConfiguration configuration,
        HttpProtocols defaultProtocols,
        bool redeclareCleartextEndpoint,
        int cleartextPort)
    {
        if (configuration.GetValue<int?>(HealthProbePortConfigKey) is not int probePort)
        {
            return [];
        }

        return redeclareCleartextEndpoint
            ? [new KestrelListenerSpec(cleartextPort, defaultProtocols), new KestrelListenerSpec(probePort, HttpProtocols.Http1)]
            : [new KestrelListenerSpec(probePort, HttpProtocols.Http1)];
    }

    /// <summary>One explicit Kestrel listener: a port and the protocols it accepts.</summary>
    /// <param name="Port">The port to listen on.</param>
    /// <param name="Protocols">The protocols accepted on that port.</param>
    internal sealed record KestrelListenerSpec(int Port, HttpProtocols Protocols);
}
