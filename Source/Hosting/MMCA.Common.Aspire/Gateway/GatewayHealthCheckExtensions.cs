using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MMCA.Common.Aspire.Gateway;

/// <summary>
/// Which HTTP version a downstream <c>/alive</c> probe asks for.
/// </summary>
public enum DownstreamProbeVersion
{
    /// <summary>
    /// Try HTTP/2 (h2c prior knowledge) first and, when the downstream refuses the protocol rather
    /// than the connection, fall back to HTTP/1.1 inside the SAME check, so a single readiness poll
    /// still produces one verdict. The version that answered is then latched for the life of the
    /// process, making the fallback a one-time cost per downstream rather than a per-poll one.
    /// </summary>
    Auto,

    /// <summary>
    /// Always send HTTP/2 over cleartext (h2c prior knowledge), with
    /// <see cref="HttpVersionPolicy.RequestVersionExact"/> so the request never negotiates down.
    /// Pin this only for a downstream known to be HTTP/2-only, to skip the one-time negotiation.
    /// </summary>
    Http2,

    /// <summary>
    /// Always send HTTP/1.1 with <see cref="HttpVersionPolicy.RequestVersionOrLower"/>, exactly the
    /// stock <see cref="HttpClient"/> behavior. Pin this only for a downstream known to be
    /// HTTP/1.1-only, to skip the one-time negotiation.
    /// </summary>
    Http11
}

/// <summary>
/// Per-call options for <c>AddGatewayDownstreamHealthChecks</c>. One instance covers every service
/// name passed to that call, so a gateway pinning a version profile for some of its heads makes one
/// call per profile rather than carrying a per-name map.
/// </summary>
public sealed class GatewayDownstreamHealthCheckOptions
{
    /// <summary>
    /// Which HTTP version the probe requests. Default <see cref="DownstreamProbeVersion.Auto"/>,
    /// which settles the question per downstream and needs no per-service configuration.
    /// <para>
    /// Negotiating is necessary because neither fixed answer is right for every head. The services
    /// a modular-monolith gateway fronts serve h2c so cross-service gRPC clients reach HTTP/2
    /// without TLS/ALPN, and an <see cref="HttpClient"/> left on its own defaults sends HTTP/1.1,
    /// which an HTTP/2-only cleartext endpoint refuses. Sending HTTP/2 unconditionally has the
    /// mirror-image failure: an HTTP/1.1-only endpoint, or a mixed <c>Http1AndHttp2</c> one serving
    /// cleartext h2 without ALPN, answers <c>HTTP_1_1_REQUIRED</c> forever. Either way the gateway
    /// reports a downstream outage that does not exist.
    /// </para>
    /// <para>
    /// Pin <see cref="DownstreamProbeVersion.Http2"/> or <see cref="DownstreamProbeVersion.Http11"/>
    /// only for a downstream whose profile is known and fixed, to skip the one-time negotiation.
    /// </para>
    /// </summary>
    public DownstreamProbeVersion ProbeVersion { get; set; } = DownstreamProbeVersion.Auto;

    /// <summary>
    /// Whether the probe speaks HTTP/2 over cleartext (h2c prior knowledge). A compatibility facade
    /// over <see cref="ProbeVersion"/>: reading it reports whether the pinned mode is
    /// <see cref="DownstreamProbeVersion.Http2"/> (so it reads <see langword="false"/> under the
    /// <see cref="DownstreamProbeVersion.Auto"/> default, which has not chosen a version yet), and
    /// writing it pins <see cref="DownstreamProbeVersion.Http2"/> or
    /// <see cref="DownstreamProbeVersion.Http11"/>, giving up the negotiation.
    /// </summary>
#pragma warning disable S1133 // Deprecated code should be removed: the obsoletion IS the migration mechanism; it flags every remaining consumer opt-out during the lockstep sweep, and the facade is removed once all consumers are swept.
    [Obsolete("Use ProbeVersion; the Auto default negotiates the protocol per downstream.")]
    public bool ProbeOverHttp2
    {
        get => ProbeVersion == DownstreamProbeVersion.Http2;
        set => ProbeVersion = value ? DownstreamProbeVersion.Http2 : DownstreamProbeVersion.Http11;
    }
#pragma warning restore S1133
}

/// <summary>
/// Registers one health check per downstream service a gateway fronts, so an edge that cannot reach
/// the services behind it stops advertising itself as ready.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class GatewayHealthCheckExtensions
{
    /// <summary>Prefix of the health-check name registered for each service.</summary>
    internal const string CheckNamePrefix = "downstream-";

    /// <summary>Prefix of the named <see cref="HttpClient"/> registered for each service.</summary>
    internal const string ClientNamePrefix = "gateway-downstream-";

    /// <summary>
    /// Probe budget. Short on purpose: this runs on every <c>/health/ready</c> poll, once per
    /// downstream service, and a probe that takes longer than the poll interval turns readiness
    /// into a queue. A service that cannot answer a liveness ping in two seconds is not one the
    /// gateway should be routing to anyway.
    /// </summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Health-check name for the given service.</summary>
    /// <param name="serviceName">The Aspire service name.</param>
    /// <returns>The registered health-check name.</returns>
    internal static string CheckName(string serviceName) => CheckNamePrefix + serviceName;

    /// <summary>Named-client name for the given service.</summary>
    /// <param name="serviceName">The Aspire service name.</param>
    /// <returns>The registered <see cref="HttpClient"/> name.</returns>
    internal static string ClientName(string serviceName) => ClientNamePrefix + serviceName;

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers a <c>downstream-{name}</c> health check for each of
        /// <paramref name="serviceNames"/>. Each check GETs <c>/alive</c> on
        /// <c>http://{name}</c> through a named <see cref="HttpClient"/>; service discovery
        /// (wired for every client by <c>AddServiceDefaults()</c>'s
        /// <c>ConfigureHttpClientDefaults</c>) resolves that scheme-and-name into the real endpoint,
        /// so nothing here hard-codes a host or port.
        /// </summary>
        /// <param name="serviceNames">
        /// The Aspire service names the gateway fronts. Duplicates and blanks are ignored, and a
        /// name already registered by an earlier call is skipped, so calling this twice does not
        /// register two checks with the same name (which the health-check service rejects at
        /// startup).
        /// </param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// The checks are tagged <see cref="HealthCheckTags.Ready"/>, which puts them on
        /// <c>/health</c> and <c>/health/ready</c> but keeps them OFF <c>/alive</c>. That split is
        /// the point: a gateway that cannot reach its services should be pulled out of the load
        /// balancer, but restarting the gateway process fixes nothing about a downstream outage, so
        /// it must never fail liveness.
        /// </para>
        /// <para>
        /// Failure status is <see cref="HealthStatus.Unhealthy"/>, not Degraded. Readiness is a
        /// binary routing decision and <c>/health/ready</c> treats Degraded as passing, so a
        /// Degraded downstream check would report a problem while still taking traffic the gateway
        /// cannot serve. A downstream the gateway genuinely can live without belongs behind
        /// <see cref="HealthCheckTags.Optional"/> instead, which excludes it from readiness
        /// altogether.
        /// </para>
        /// </remarks>
        public IServiceCollection AddGatewayDownstreamHealthChecks(params string[] serviceNames) =>
            Register(services, configure: null, serviceNames);

        /// <summary>
        /// Same registration as <c>AddGatewayDownstreamHealthChecks(params string[])</c>, with the
        /// probe's HTTP version profile under the caller's control.
        /// </summary>
        /// <param name="configure">
        /// Configures the options applied to every name in THIS call. The
        /// <see cref="DownstreamProbeVersion.Auto"/> default fits every downstream; pass
        /// <c>o =&gt; o.ProbeVersion = DownstreamProbeVersion.Http11</c> only to pin a head whose
        /// profile is already known and skip its one-time negotiation.
        /// </param>
        /// <param name="serviceNames">
        /// The Aspire service names the gateway fronts. Duplicates and blanks are ignored, and a
        /// name already registered by an earlier call is skipped.
        /// </param>
        /// <returns>The same service collection for chaining.</returns>
        public IServiceCollection AddGatewayDownstreamHealthChecks(
            Action<GatewayDownstreamHealthCheckOptions>? configure,
            params string[] serviceNames) =>
            Register(services, configure, serviceNames);
    }

    /// <summary>
    /// The single registration body behind both public overloads.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="configure">Optional per-call option mutator.</param>
    /// <param name="serviceNames">The Aspire service names the gateway fronts.</param>
    /// <returns>The same service collection for chaining.</returns>
    private static IServiceCollection Register(
        IServiceCollection services,
        Action<GatewayDownstreamHealthCheckOptions>? configure,
        string[] serviceNames)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceNames);

        var options = new GatewayDownstreamHealthCheckOptions();
        configure?.Invoke(options);

        // Captured as a local so the registration closure below does not hold the mutable options
        // object: a later call reusing the same instance must not retro-change these checks.
        var probeVersion = options.ProbeVersion;

        var registry = GatewayDownstreamRegistry.GetOrAdd(services);
        var healthChecks = services.AddHealthChecks();

        foreach (var serviceName in serviceNames)
        {
            if (string.IsNullOrWhiteSpace(serviceName) || !registry.TryClaim(serviceName))
            {
                continue;
            }

            var clientName = ClientName(serviceName);
            var name = serviceName;

            services.AddHttpClient(clientName, client =>
            {
                // "http://{name}" is the service-discovery form: the resolver rewrites it to the
                // real endpoint. The client timeout is the real probe budget; the registration
                // timeout below only bounds the health-check service's own wait.
                client.BaseAddress = new Uri("http://" + name, UriKind.Absolute);
                client.Timeout = ProbeTimeout;

                // The HTTP version is deliberately NOT pinned on the client. It belongs to the
                // request, because under DownstreamProbeVersion.Auto the check discovers which
                // version this downstream speaks and may send both on one poll. See
                // GatewayDownstreamHealthCheckOptions.ProbeVersion.
            });

            healthChecks.Add(new HealthCheckRegistration(
                CheckName(name),
                sp => new DownstreamServiceHealthCheck(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    name,
                    clientName,
                    probeVersion),
                failureStatus: HealthStatus.Unhealthy,
                tags: [HealthCheckTags.Ready],
                timeout: ProbeTimeout));
        }

        return services;
    }
}

/// <summary>
/// Registration-time ledger of the downstream services already wired by
/// <c>AddGatewayDownstreamHealthChecks</c>. It exists because <see cref="IServiceCollection"/>
/// offers no way to ask which health checks are already registered, and a duplicate health-check
/// NAME is a startup exception rather than a harmless second registration.
/// </summary>
internal sealed class GatewayDownstreamRegistry
{
    /// <summary>
    /// Service names already registered. A list rather than a case-insensitive
    /// <see cref="HashSet{T}"/> because a gateway fronts a handful of services, and the set's
    /// comparer-carrying constructor is one of the initializer shapes IDE0028 misreports here.
    /// </summary>
    private readonly List<string> _names = [];

    /// <summary>
    /// Records <paramref name="serviceName"/> as registered, returning <see langword="false"/> when
    /// an earlier call already claimed it (compared case-insensitively).
    /// </summary>
    /// <param name="serviceName">The Aspire service name.</param>
    /// <returns><see langword="true"/> when this is the first registration for the name.</returns>
    internal bool TryClaim(string serviceName)
    {
        if (_names.Contains(serviceName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _names.Add(serviceName);
        return true;
    }

    /// <summary>
    /// Returns the ledger already attached to <paramref name="services"/>, adding one if this is the
    /// first call.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <returns>The single ledger instance for this collection.</returns>
    internal static GatewayDownstreamRegistry GetOrAdd(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(GatewayDownstreamRegistry)
                && descriptor.ImplementationInstance is GatewayDownstreamRegistry existing)
            {
                return existing;
            }
        }

        var registry = new GatewayDownstreamRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}
