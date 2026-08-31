using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MMCA.Common.Aspire.Hosting;

/// <summary>
/// AppHost-side health check for a project resource whose cleartext endpoint is Http2-only (h2c,
/// prior knowledge), so a <c>WaitFor</c> edge pointed at that resource waits for the service to be
/// READY rather than merely started.
/// <para>
/// Aspire's stock <c>WithHttpHealthCheck</c> probes with a default <see cref="HttpClient"/>, which
/// sends HTTP/1.1. A Kestrel endpoint configured <c>HttpProtocols.Http2</c> answers that with GOAWAY
/// <c>HTTP_1_1_REQUIRED</c> rather than the health payload, so the check never turns healthy and the
/// resource cannot be health-gated at all. Every <c>WaitFor</c> edge into it then degrades to "the
/// process started", which is precisely the condition that lets a dependent resource race a service
/// that has not finished migrating, seeding or warming up. This registration issues the same GET
/// over HTTP/2 with <see cref="HttpVersionPolicy.RequestVersionExact"/>, the h2c prior-knowledge
/// profile the framework's own gateway probes already use.
/// </para>
/// <para>
/// <b>Rejected alternative, recorded so it is not re-proposed: surfacing the HTTP/1.1 health-probe
/// listener as an Aspire endpoint.</b> A deployed service already runs a dedicated Http1-only
/// listener for the platform probes (<c>ConfigureEndpointsWithHealthProbe</c>, driven by the
/// <c>HealthProbe:Port</c> configuration key), so it looks tempting to inject
/// <c>HealthProbe__Port</c> locally as well and point a stock <c>WithHttpHealthCheck</c> at it.
/// Injecting that variable locally flips <c>ConfigureEndpointsWithHealthProbe</c> out of
/// endpoint-defaults mode and into explicit-listener mode: its <c>Listen</c> calls then override the
/// <c>ASPNETCORE_URLS</c> binding Aspire injects, so the service stops listening on the dynamic port
/// Aspire allocated for it, and every co-hosted service collides on the one fixed cleartext port.
/// The probe listener is a deployment-only construct. Locally, the answer is to speak the protocol
/// the service already serves.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class H2cHealthCheckExtensions
{
    /// <summary>
    /// Default path probed. <c>/alive</c> rather than <c>/health/ready</c> on purpose: a startup
    /// <c>WaitFor</c> gate must probe LIVENESS, never readiness. A readiness endpoint aggregates
    /// downstream and warmup checks, so gating startup on it can deadlock the dependency graph: the
    /// gateway waits for a service to report ready, that service's readiness includes a check that
    /// warms up through the gateway, and neither side can complete first. Liveness answers as soon as
    /// the process can serve a request, which is exactly what a dependent resource needs to know
    /// before it starts. Pass an explicit path when a specific resource genuinely needs a stricter
    /// gate and no cycle exists.
    /// </summary>
    public const string DefaultProbePath = "/alive";

    /// <summary>
    /// Default Aspire endpoint name probed. The cleartext <c>http</c> endpoint is the h2c one; an
    /// <c>https</c> endpoint negotiates the version through ALPN and needs nothing special.
    /// </summary>
    public const string DefaultEndpointName = "http";

    /// <summary>
    /// Probe budget, matching the gateway's downstream probes. Short on purpose: Aspire polls this
    /// check for as long as a dependent resource is waiting, and a probe slower than the poll
    /// interval turns the wait into a queue.
    /// </summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Health-check key registered for one resource-and-endpoint pair. The endpoint name is part of
    /// the key because a resource may expose more than one endpoint worth gating.
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="endpointName">The Aspire endpoint name being probed.</param>
    /// <returns>The health-check key.</returns>
    internal static string CheckKey(string resourceName, string endpointName) =>
        resourceName + "-h2c-" + endpointName;

    extension(IResourceBuilder<ProjectResource> builder)
    {
        /// <summary>
        /// Registers an HTTP/2 (h2c prior knowledge) health check against one of the resource's
        /// endpoints and associates it with the resource, so <c>WaitFor</c> edges into this resource
        /// wait for a real answer over the protocol the endpoint actually speaks.
        /// <para>
        /// Use this instead of Aspire's <c>WithHttpHealthCheck</c> whenever the target endpoint is
        /// Http2-only. For an endpoint that serves <c>Http1AndHttp2</c> the stock extension is fine
        /// and this one is unnecessary.
        /// </para>
        /// <para>
        /// <b>Probe liveness, not readiness.</b> The default path is <c>/alive</c> for that reason: a
        /// readiness endpoint rolls up downstream and warmup checks, and a startup gate pointed at one
        /// can deadlock the dependency graph when the warmup path runs back through the resource that
        /// is doing the waiting. Only override <paramref name="path"/> when the stricter gate is
        /// genuinely required and no such cycle exists.
        /// </para>
        /// <para>
        /// Calling it twice for the same resource and endpoint is a no-op: the second call returns
        /// the builder unchanged rather than registering a duplicate health-check key, which the
        /// health-check service rejects at startup.
        /// </para>
        /// </summary>
        /// <param name="path">The path to GET (defaults to <see cref="DefaultProbePath"/>).</param>
        /// <param name="endpointName">
        /// The Aspire endpoint to probe (defaults to <see cref="DefaultEndpointName"/>). The endpoint
        /// is resolved lazily, so a name the resource never declares is not an error here: it
        /// surfaces as a permanently unhealthy check, which keeps the dependent resource waiting
        /// instead of letting it start against a service nobody verified.
        /// </param>
        /// <returns>The project resource builder for chaining.</returns>
        public IResourceBuilder<ProjectResource> WithH2cHealthCheck(
            string path = DefaultProbePath,
            string endpointName = DefaultEndpointName)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);

            var services = builder.ApplicationBuilder.Services;
            var key = CheckKey(builder.Resource.Name, endpointName);

            if (!H2cHealthCheckRegistry.GetOrAdd(services).TryClaim(key))
            {
                return builder;
            }

            var endpoint = builder.GetEndpoint(endpointName);

            services.AddHealthChecks().Add(new HealthCheckRegistration(
                key,
                _ => new H2cEndpointHealthCheck(endpoint, path),
                failureStatus: HealthStatus.Unhealthy,
                tags: null,
                timeout: ProbeTimeout));

            // WithHealthCheck only ASSOCIATES the named check with the resource; the registration
            // above is what supplies it. Both halves are required for a WaitFor edge to gate.
            return builder.WithHealthCheck(key);
        }
    }
}

/// <summary>
/// Registration-time ledger of the resource-and-endpoint pairs already wired by
/// <c>WithH2cHealthCheck</c>. It exists because <see cref="IServiceCollection"/> offers no way to ask
/// which health checks are already registered, and a duplicate health-check key is a startup
/// exception rather than a harmless second registration.
/// <para>
/// The ledger is attached to the AppHost's own service collection rather than kept in a static field,
/// so two <c>DistributedApplication.CreateBuilder</c> instances in one process (which is what a test
/// run is) never see each other's claims.
/// </para>
/// </summary>
internal sealed class H2cHealthCheckRegistry
{
    /// <summary>
    /// Keys already registered. A list rather than a case-insensitive <see cref="HashSet{T}"/>
    /// because an AppHost gates a handful of resources, and the set's comparer-carrying constructor
    /// is one of the initializer shapes IDE0028 misreports here.
    /// </summary>
    private readonly List<string> _keys = [];

    /// <summary>
    /// Records <paramref name="key"/> as registered, returning <see langword="false"/> when an
    /// earlier call already claimed it (compared case-insensitively).
    /// </summary>
    /// <param name="key">The health-check key.</param>
    /// <returns><see langword="true"/> when this is the first registration for the key.</returns>
    internal bool TryClaim(string key)
    {
        if (_keys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _keys.Add(key);
        return true;
    }

    /// <summary>
    /// Returns the ledger already attached to <paramref name="services"/>, adding one if this is the
    /// first call.
    /// </summary>
    /// <param name="services">The AppHost service collection being configured.</param>
    /// <returns>The single ledger instance for this collection.</returns>
    internal static H2cHealthCheckRegistry GetOrAdd(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(H2cHealthCheckRegistry)
                && descriptor.ImplementationInstance is H2cHealthCheckRegistry existing)
            {
                return existing;
            }
        }

        var registry = new H2cHealthCheckRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}
