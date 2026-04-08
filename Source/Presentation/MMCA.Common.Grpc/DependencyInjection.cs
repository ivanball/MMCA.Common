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
        ///   <item>Service discovery: address resolved as <c>http://{serviceName}</c>.</item>
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

            // AddStandardResilienceHandler returns IHttpStandardResiliencePipelineBuilder; the
            // pipeline is wired onto the same IHttpClientBuilder, so return the original builder
            // for chaining further customization (e.g., additional message handlers).
            builder.AddStandardResilienceHandler();
            return builder;
        }
    }
}
