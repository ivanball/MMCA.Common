using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;

namespace MMCA.Common.Aspire.Hosting;

/// <summary>
/// The front-door wiring every gateway-fronted AppHost repeats: the YARP gateway over the extracted
/// services, the UI head over the gateway, and the UI's two API endpoint settings, each as one call.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class GatewayUiExtensions
{
    private const string HttpsEndpointName = "https";

    extension(IResourceBuilder<DotnetProjectResource> gateway)
    {
        /// <summary>
        /// Wires the gateway resource over the services it fronts: a reference to and a wait on each
        /// service (in the order given), external HTTP endpoints, a liveness startup probe
        /// (<c>/alive</c>) and the <c>https</c> endpoint pinned to <paramref name="httpsPort"/>, the
        /// well-known URL every client and the E2E suite dial.
        /// <para>
        /// The startup probe is <c>/alive</c>, never <c>/health/ready</c>: the gateway's readiness
        /// aggregate includes one probe per downstream service, so a startup gate on it means any one
        /// unreachable downstream stops <c>ui.WaitFor(gateway)</c> from ever releasing. That was the
        /// 2026-08-29 UI WaitFor wedge: the stack never became ready and a whole E2E run reported zero
        /// tests executed. Liveness proves what a wait needs (Kestrel up and serving) without pulling
        /// cross-resource readiness into the startup order. Deployed environments own their own
        /// probes; the ADC and Store Container Apps point the gateway's readiness probe at <c>/alive</c>
        /// as well, and <c>/health/ready</c> stays a reporting endpoint.
        /// </para>
        /// </summary>
        /// <param name="httpsPort">The fixed host port of the gateway's <c>https</c> endpoint.</param>
        /// <param name="services">The service resources the gateway references and waits for.</param>
        /// <returns>The same gateway resource builder, for chaining.</returns>
        public IResourceBuilder<DotnetProjectResource> WithMmcaGateway(
            int httpsPort,
            params IResourceBuilder<DotnetProjectResource>[] services)
        {
            ArgumentNullException.ThrowIfNull(gateway);
            ArgumentNullException.ThrowIfNull(services);

            foreach (var service in services)
            {
                ArgumentNullException.ThrowIfNull(service);
                gateway.WithReference(service).WaitFor(service);
            }

            return gateway
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck(H2cHealthCheckExtensions.DefaultProbePath)
                .WithEndpoint(HttpsEndpointName, endpoint => endpoint.Port = httpsPort);
        }
    }

    extension(IResourceBuilder<DotnetProjectResource> ui)
    {
        /// <summary>
        /// Wires the UI head over the gateway and the services: external HTTP endpoints, a reference to
        /// and a wait on the gateway, then a reference to and a wait on each service (in the order
        /// given), a readiness health check (<c>/health/ready</c>) and the <c>https</c> endpoint pinned
        /// to <paramref name="httpsPort"/>, so the E2E suite and any client outside Aspire reach the UI
        /// at a well-known URL. The UI project must be added with its <c>https</c> launch profile so
        /// that endpoint exists to pin.
        /// <para>
        /// Unlike every service and the gateway, the UI gates on READINESS: it runs the default
        /// Http1AndHttp2 profile and maps the readiness endpoint itself, and nothing in the AppHost
        /// waits for it, so a readiness check cannot form a startup cycle. Anything waiting on the UI
        /// (the E2E suite, a developer's browser) then gates on a real 200 rather than on the process
        /// being up.
        /// </para>
        /// </summary>
        /// <param name="gateway">The gateway resource the UI calls.</param>
        /// <param name="httpsPort">The fixed host port of the UI's <c>https</c> endpoint.</param>
        /// <param name="services">The service resources the UI references and waits for.</param>
        /// <returns>The same UI resource builder, for chaining.</returns>
        public IResourceBuilder<DotnetProjectResource> WithMmcaUi(
            IResourceBuilder<DotnetProjectResource> gateway,
            int httpsPort,
            params IResourceBuilder<DotnetProjectResource>[] services)
        {
            ArgumentNullException.ThrowIfNull(ui);
            ArgumentNullException.ThrowIfNull(gateway);
            ArgumentNullException.ThrowIfNull(services);

            ui.WithExternalHttpEndpoints()
                .WithReference(gateway)
                .WaitFor(gateway);

            foreach (var service in services)
            {
                ArgumentNullException.ThrowIfNull(service);
                ui.WithReference(service).WaitFor(service);
            }

            return ui
                .WithHttpHealthCheck("/health/ready")
                .WithEndpoint(HttpsEndpointName, endpoint => endpoint.Port = httpsPort);
        }

        /// <summary>
        /// Sets both of the UI's API endpoints, <c>Api__ApiEndpoint</c> and <c>Api__WasmApiEndpoint</c>,
        /// to the gateway's <c>https</c> endpoint, derived from the gateway resource rather than
        /// hardcoded. Adds no reference and no wait.
        /// <para>
        /// <c>Api__WasmApiEndpoint</c> is what the UI hands the BROWSER (and what the Blazor CSP pins
        /// <c>connect-src</c> to), so it must be a URL the browser can reach: a browser cannot resolve
        /// an Aspire service-discovery name. <c>Api__ApiEndpoint</c> is the server head's own value,
        /// whose appsettings default is the service-discovery form (<c>http://gateway</c>); every
        /// <c>IHttpClientFactory</c> client resolves that name, but a SignalR <c>HubConnection</c>
        /// builds its own message handler and never sees the discovery handler, so under Aspire it
        /// needs the real endpoint too.
        /// </para>
        /// </summary>
        /// <param name="gateway">The gateway resource whose <c>https</c> endpoint the UI calls.</param>
        /// <returns>The same UI resource builder, for chaining.</returns>
        public IResourceBuilder<DotnetProjectResource> WithGatewayApiEndpoints(IResourceBuilder<DotnetProjectResource> gateway)
        {
            ArgumentNullException.ThrowIfNull(ui);
            ArgumentNullException.ThrowIfNull(gateway);

            var gatewayHttps = gateway.GetEndpoint(HttpsEndpointName);

            return ui
                .WithEnvironment("Api__ApiEndpoint", gatewayHttps)
                .WithEnvironment("Api__WasmApiEndpoint", gatewayHttps);
        }
    }
}
