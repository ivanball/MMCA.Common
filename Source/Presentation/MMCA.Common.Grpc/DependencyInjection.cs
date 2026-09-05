using Grpc.Net.ClientFactory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Grpc.Interceptors;
using MMCA.Common.Shared.Resilience;

namespace MMCA.Common.Grpc;

/// <summary>
/// gRPC infrastructure registration. Provides server-side defaults
/// (<see cref="GrpcResultExceptionInterceptor"/> and server reflection) and a
/// typed-client convention that wires Aspire service discovery, Polly resilience, and
/// JWT bearer forwarding via <see cref="JwtForwardingClientInterceptor"/>.
/// </summary>
/// <remarks>
/// This package is the extraction boundary for a module lifted out of the modular monolith: it
/// carries transport concerns only. The h2c (HTTP/2 cleartext) address scheme used by
/// <c>AddTypedGrpcClient</c> is deliberate for in-cluster service-to-service calls (the Aspire
/// service-discovery rationale is on that method). Consuming modules keep the generated protobuf
/// types out of their application and domain code by placing a hand-written adapter between the
/// typed client and their own interface contract: that adapter is the module's Anti-Corruption
/// Layer (ADR-007), and the monolith-to-service move itself follows the Strangler Fig route
/// (ADR-008).
/// </remarks>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers gRPC server services with the MMCA defaults: the
        /// <see cref="GrpcResultExceptionInterceptor"/> for translating <c>Result</c> failures
        /// to <c>RpcException</c>, and server reflection so tools like grpcurl can introspect the
        /// schema.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddGrpcServiceDefaults()
        {
            services.TryAddSingleton<GrpcResultExceptionInterceptor>();

            services.AddGrpc(options =>
            {
                options.Interceptors.Add<GrpcResultExceptionInterceptor>();
                options.EnableDetailedErrors = false;
            });

            services.AddGrpcReflection();
            return services;
        }

        /// <summary>
        /// Registers a typed gRPC client (<typeparamref name="TClient"/>) targeted at the named
        /// service via Aspire service discovery. The client is wired with:
        /// <list type="bullet">
        ///   <item>Service discovery: address resolved as <c>http://{serviceName}</c> — HTTP/2
        ///   cleartext (h2c) with prior knowledge. We use h2c rather than HTTPS because Aspire's
        ///   project-resource endpoint discovery from <c>launchSettings.json</c> doesn't reliably
        ///   create a <c>services__&lt;name&gt;__https__0</c> discovery key for project resources;
        ///   the resolver silently falls back to <c>http</c> regardless of the requested scheme.
        ///   The target service must serve HTTP/2 on its cleartext endpoint via
        ///   <c>"Kestrel": { "EndpointDefaults": { "Protocols": "Http2" } }</c> in its
        ///   <c>appsettings.json</c> — otherwise Kestrel rejects HTTP/2 frames with
        ///   <c>HTTP_1_1_REQUIRED</c>.</item>
        ///   <item><see cref="JwtForwardingClientInterceptor"/>: forwards inbound bearer tokens.</item>
        ///   <item>Standard Polly resilience handler: matches the HTTP defaults from <c>MMCA.Common.Aspire</c>.</item>
        /// </list>
        /// <para>
        /// Generated gRPC client classes (e.g. <c>Catalog.V1.ProductVariantService.ProductVariantServiceClient</c>)
        /// can be passed as <typeparamref name="TClient"/>. Application code should not consume that
        /// generated client directly: register a hand-written adapter that implements the consuming
        /// module's own C# interface contract (<c>IProductVariantService</c>) and delegates to this
        /// typed gRPC client. That adapter IS the consuming module's Anti-Corruption Layer: the only
        /// place where the peer's wire model (the generated protobuf types) is translated into the
        /// module's own interface contract and domain types, so the peer's contract never leaks
        /// inward past it. ADR-007 names the pattern
        /// (https://ivanball.github.io/docs/adr/007-grpc-extraction.html).
        /// </para>
        /// <para>
        /// Extraction itself follows the Strangler Fig route recorded in ADR-008
        /// (https://ivanball.github.io/docs/adr/008-service-extraction-topology.html): the new service
        /// host is stood up beside the modular monolith, the typed client and its Anti-Corruption
        /// Layer adapter move traffic to it, and the in-process path is retired last.
        /// </para>
        /// </summary>
        /// <typeparam name="TClient">The generated gRPC client class.</typeparam>
        /// <param name="serviceName">The Aspire service-discovery name (e.g. <c>"catalog"</c>).</param>
        /// <returns>The <see cref="IHttpClientBuilder"/> for further customization.</returns>
        public IHttpClientBuilder AddTypedGrpcClient<TClient>(string serviceName)
            where TClient : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

            services.AddHttpContextAccessor();
            services.TryAddTransient<JwtForwardingClientInterceptor>();

#pragma warning disable S5332 // Deliberate h2c (HTTP/2 cleartext) gRPC transport for in-cluster service-to-service calls; see the class-level extraction boundary remarks
            var builder = services.AddGrpcClient<TClient>(options =>
                    options.Address = new Uri($"http://{serviceName}"))
                .AddInterceptor<JwtForwardingClientInterceptor>(InterceptorScope.Client);
#pragma warning restore S5332

            // Force the primary handler to a SocketsHttpHandler that explicitly opts into
            // HTTP/2. The global ConfigureHttpClientDefaults from MMCA.Common.Aspire applies
            // to ALL HttpClients including the gRPC client, and its standard resilience
            // pipeline can wrap the primary handler in a way that defeats HTTP/2 negotiation
            // (the default HttpClientHandler doesn't always honor HTTP/2 preference even when
            // the request specifies Version=2.0). Setting SocketsHttpHandler explicitly
            // bypasses that wrapper for the gRPC client only — so the connection-hygiene
            // values (pooled lifetime for ACA replica-rollover DNS pickup, keep-alive pings)
            // must be re-applied here from the same HttpResilienceDefaults source of truth.
            builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime = HttpResilienceDefaults.PooledConnectionLifetime,
                PooledConnectionIdleTimeout = HttpResilienceDefaults.PooledConnectionIdleTimeout,
                KeepAlivePingDelay = HttpResilienceDefaults.KeepAlivePingDelay,
                KeepAlivePingTimeout = HttpResilienceDefaults.KeepAlivePingTimeout,
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            });

            // AddStandardResilienceHandler returns IHttpStandardResiliencePipelineBuilder; the
            // pipeline is wired onto the same IHttpClientBuilder, so return the original builder
            // for chaining further customization (e.g., additional message handlers). Every value
            // comes from GrpcResilienceDefaults: its timeouts and retry budget are the same ones
            // MMCA.Common.Aspire's ConfigureHttpClientDefaults applies (they previously drifted to
            // the 10s/30s library defaults), and its circuit-breaker values are explicit because an
            // east-west gRPC call bypasses the Gateway's active health checks.
            builder.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = GrpcResilienceDefaults.AttemptTimeout;
                options.TotalRequestTimeout.Timeout = GrpcResilienceDefaults.TotalRequestTimeout;
                options.Retry.MaxRetryAttempts = GrpcResilienceDefaults.MaxRetryAttempts;
                options.CircuitBreaker.SamplingDuration = GrpcResilienceDefaults.SamplingDuration;
                options.CircuitBreaker.FailureRatio = GrpcResilienceDefaults.FailureRatio;
                options.CircuitBreaker.MinimumThroughput = GrpcResilienceDefaults.MinimumThroughput;
                options.CircuitBreaker.BreakDuration = GrpcResilienceDefaults.BreakDuration;
            });
            return builder;
        }
    }
}
