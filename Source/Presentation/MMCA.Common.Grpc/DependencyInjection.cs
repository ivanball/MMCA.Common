using Grpc.Net.ClientFactory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using MMCA.Common.Grpc.Interceptors;

namespace MMCA.Common.Grpc;

/// <summary>
/// gRPC infrastructure registration. Provides server-side defaults
/// (<see cref="GrpcResultExceptionInterceptor"/>, reflection, response compression) and a
/// typed-client convention that wires Aspire service discovery, Polly resilience, and
/// JWT bearer forwarding via <see cref="JwtForwardingClientInterceptor"/>.
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers gRPC server services with the MMCA defaults: the
        /// <see cref="GrpcResultExceptionInterceptor"/> for translating <c>Result</c> failures
        /// to <c>RpcException</c>, server reflection so tools like grpcurl can introspect the
        /// schema, and standard response compression.
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
        /// can be passed as <typeparamref name="TClient"/>. Application code should typically
        /// register a hand-written adapter that implements the C# interface contract
        /// (<c>IProductVariantService</c>) and delegates to this typed gRPC client.
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

            var builder = services.AddGrpcClient<TClient>(options =>
                    options.Address = new Uri($"http://{serviceName}"))
                .AddInterceptor<JwtForwardingClientInterceptor>(InterceptorScope.Client);

            // Force the primary handler to a SocketsHttpHandler that explicitly opts into
            // HTTP/2. The global ConfigureHttpClientDefaults from MMCA.Common.Aspire applies
            // to ALL HttpClients including the gRPC client, and its standard resilience
            // pipeline can wrap the primary handler in a way that defeats HTTP/2 negotiation
            // (the default HttpClientHandler doesn't always honor HTTP/2 preference even when
            // the request specifies Version=2.0). Setting SocketsHttpHandler explicitly
            // bypasses that wrapper for the gRPC client only.
            builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            });

            // AddStandardResilienceHandler returns IHttpStandardResiliencePipelineBuilder; the
            // pipeline is wired onto the same IHttpClientBuilder, so return the original builder
            // for chaining further customization (e.g., additional message handlers).
            builder.AddStandardResilienceHandler();
            return builder;
        }
    }
}
