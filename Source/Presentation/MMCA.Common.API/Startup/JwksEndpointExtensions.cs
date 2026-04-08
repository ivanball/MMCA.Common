using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Infrastructure.Auth;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Maps the <c>/.well-known/jwks.json</c> endpoint that exposes the active
/// <see cref="JsonWebKeySet"/> of the Identity service. Other services point their
/// JWT bearer middleware at this endpoint via <c>AddForwardedJwtBearer</c>.
/// </summary>
public static class JwksEndpointExtensions
{
    /// <summary>
    /// Default JWKS path per RFC 7517 / RFC 8615 (well-known URIs).
    /// </summary>
    public const string DefaultJwksPath = "/.well-known/jwks.json";

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps the JWKS endpoint at <see cref="DefaultJwksPath"/>. The endpoint resolves
        /// <see cref="IJwksProvider"/> from DI and serializes the result as
        /// <c>application/json</c>. Anonymous access is allowed because JWKS is a public
        /// endpoint by definition — clients fetch it before they have a token.
        /// </summary>
        /// <returns>The endpoint route builder for chaining.</returns>
        public IEndpointRouteBuilder MapJwksEndpoint()
        {
            endpoints.MapGet(DefaultJwksPath, (HttpContext context, IJwksProvider provider) =>
            {
                var keySet = provider.GetJsonWebKeySet();
                var json = JsonSerializer.Serialize(keySet);
                context.Response.ContentType = "application/json; charset=utf-8";
                return context.Response.WriteAsync(json);
            }).AllowAnonymous();

            return endpoints;
        }
    }
}
