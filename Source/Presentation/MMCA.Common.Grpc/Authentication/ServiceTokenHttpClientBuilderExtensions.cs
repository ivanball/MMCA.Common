using Grpc.Net.ClientFactory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Grpc.Interceptors;

namespace MMCA.Common.Grpc.Authentication;

/// <summary>
/// Per-client opt-in for service tokens on a typed gRPC client.
/// </summary>
public static class ServiceTokenHttpClientBuilderExtensions
{
    extension(IHttpClientBuilder builder)
    {
        /// <summary>
        /// Adds <see cref="ServiceTokenClientInterceptor"/> to this client only, so a call it makes with
        /// no user token is sent as this service. Chain it onto <c>AddTypedGrpcClient&lt;TClient&gt;</c>
        /// for a client whose server validates service tokens (<c>AddServiceTokenAuthentication</c>);
        /// leave it off every other client, since a server that does not validate them would have to
        /// reject the token.
        /// </summary>
        /// <remarks>
        /// The host must also call <c>AddServiceTokenClient</c>, which registers the
        /// <see cref="IServiceTokenProvider"/> the interceptor mints from; without it the client fails
        /// to resolve on first use, naming the missing provider.
        /// </remarks>
        /// <returns>The same builder, for further chaining.</returns>
        public IHttpClientBuilder WithServiceToken()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Services.AddHttpContextAccessor();
            builder.Services.TryAddTransient<ServiceTokenClientInterceptor>();
            return builder.AddInterceptor<ServiceTokenClientInterceptor>(InterceptorScope.Client);
        }
    }
}
