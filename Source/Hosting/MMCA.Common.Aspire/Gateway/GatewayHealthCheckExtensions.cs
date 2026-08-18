using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MMCA.Common.Aspire.Gateway;

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
        public IServiceCollection AddGatewayDownstreamHealthChecks(params string[] serviceNames)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(serviceNames);

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
                });

                healthChecks.Add(new HealthCheckRegistration(
                    CheckName(name),
                    sp => new DownstreamServiceHealthCheck(
                        sp.GetRequiredService<IHttpClientFactory>(),
                        name,
                        clientName),
                    failureStatus: HealthStatus.Unhealthy,
                    tags: [HealthCheckTags.Ready],
                    timeout: ProbeTimeout));
            }

            return services;
        }
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
