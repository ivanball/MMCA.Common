using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Maps a minimal OpenID Connect discovery document at
/// <c>/.well-known/openid-configuration</c>. The JWT bearer middleware in downstream
/// services fetches this when <c>AddForwardedJwtBearer</c> sets an
/// <see cref="Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions.Authority"/>.
/// The response contains just enough for token validation: the <c>issuer</c> and
/// <c>jwks_uri</c> fields.
/// <para>
/// When <c>Jwt:Issuer</c> is not configured (non-Identity services), the endpoint
/// returns <c>404 Not Found</c> — safe because no downstream service points its
/// authority at a non-Identity host.
/// </para>
/// </summary>
public static class OidcDiscoveryEndpointExtensions
{
    /// <summary>
    /// Default OpenID Configuration path per RFC 8615 (well-known URIs).
    /// </summary>
    public const string DefaultOidcDiscoveryPath = "/.well-known/openid-configuration";

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps the OpenID Connect discovery endpoint at <see cref="DefaultOidcDiscoveryPath"/>.
        /// Returns the JWT issuer and JWKS URI so downstream services can discover signing
        /// keys automatically. Anonymous access is required because the middleware fetches
        /// this document before any token is available.
        /// </summary>
        /// <returns>The endpoint route builder for chaining.</returns>
        public IEndpointRouteBuilder MapOidcDiscoveryEndpoint()
        {
            // OIDC discovery field names are snake_case per RFC 8414. Disabling the naming
            // policy preserves the exact C# property names (already OIDC snake_case) —
            // without this, Results.Json would camelCase "jwks_uri" to "jwksUri", which
            // OpenIdConnectConfigurationRetriever wouldn't recognise.
            var oidcJsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = null };

            endpoints.MapGet(DefaultOidcDiscoveryPath, (HttpContext context, IConfiguration configuration) =>
            {
                var issuer = configuration["Jwt:Issuer"];
                if (string.IsNullOrWhiteSpace(issuer))
                {
                    return Results.NotFound();
                }

                var jwksUri = $"{context.Request.Scheme}://{context.Request.Host}{JwksEndpointExtensions.DefaultJwksPath}";

                return Results.Json(new
                {
                    issuer,
                    jwks_uri = jwksUri,
                    response_types_supported = new[] { "token" },
                    subject_types_supported = new[] { "public" },
                    id_token_signing_alg_values_supported = new[] { "RS256" },
                }, oidcJsonOptions);
            }).AllowAnonymous();

            return endpoints;
        }
    }
}
